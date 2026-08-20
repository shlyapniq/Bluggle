using System.Diagnostics;
using System.Runtime.InteropServices;
using static Bluggle.Bluetooth.NativeMethods;

namespace Bluggle.Bluetooth;

/// <summary>Tunables for a connect attempt, sourced from config.json.</summary>
public sealed record ConnectOptions(
    Guid[] Services,
    int ConnectTimeoutMs,
    int LinkRetryIntervalMs,
    bool EnableMissingProfiles);

/// <summary>
/// All Bluetooth work lives here. Every public method blocks and is meant to be called from
/// a background thread; nothing in this class touches the UI.
///
/// The whole job of this class is to do what the Connect and Disconnect buttons in Windows'
/// own Bluetooth settings do, and nothing more: bring the radio link up, or tear it down. It
/// deliberately never switches a profile off.
///
/// Why that matters. The obvious way to script a disconnect is
/// BluetoothSetServiceState(..., BLUETOOTH_SERVICE_DISABLE), and it does disconnect -- by
/// uninstalling the profile. Windows tears down the bthenum child device, which removes the
/// A2DP and Hands-Free audio endpoints from the system. Re-enabling reinstalls them as *new*
/// endpoints, so every toggle leaves another dead "Disconnected" entry in the Sound control
/// panel and throws away everything attached to the old one: default-device choice, levels,
/// enhancements, exclusive-mode flags, and any app's remembered endpoint id. The Settings
/// buttons never do that, so neither do we.
///
/// Instead:
///   Connect    -- open an AF_BTH / RFCOMM socket at the device. Winsock runs an SDP query to
///                 resolve the channel, and SDP needs an ACL link, so the link comes up whether
///                 or not the remote ever accepts the channel. That is the wake-up. The
///                 already-installed profile drivers then attach on their own, exactly as they
///                 do when the device connects itself. Retried on an interval, because a device
///                 coming out of deep sleep often ignores the first attempt.
///   Disconnect -- IOCTL_BTH_DISCONNECT_DEVICE on the radio, which drops the ACL link and
///                 leaves every profile installed and enabled.
/// </summary>
public sealed class BluetoothAudioController : IDisposable
{
    private readonly object _gate = new();
    private readonly List<BluetoothRadioHandle> _radios = new();
    private int _operationDepth;
    private bool _winsockReady;
    private bool _disposed;

    /// <summary>
    /// Winsock error from the most recent link attempt, or 0 if the last one succeeded. Written
    /// from the fire-and-forget attempt task and read by the timeout path, hence volatile. This
    /// exists because swallowing it once cost an afternoon: a malformed sockaddr made every
    /// attempt fail in under a millisecond, and the only symptom was a connect that timed out
    /// as if the earbuds were asleep.
    /// </summary>
    private volatile int _lastLinkError;

    /// <summary>How often we re-check fConnected while an operation is in flight.</summary>
    private const int OperationPollMs = 300;

    // ------------------------------------------------------------------- radios ----

    public bool HasRadio
    {
        get { lock (_gate) return _radios.Count > 0; }
    }

    /// <summary>
    /// (Re)opens handles to every local radio. Cheap, and the only way to notice that the
    /// user flipped the Bluetooth toggle in Settings: when the radio is off, enumeration
    /// returns nothing at all.
    /// </summary>
    public void RefreshRadios()
    {
        lock (_gate)
        {
            // Never swap handles out from under an in-flight connect or disconnect. Those hold
            // a raw radio handle for their whole duration (seconds), and closing it mid-flight
            // would turn the rest of the operation into ERROR_INVALID_HANDLE failures. The
            // periodic device re-scan and a manual "Refresh devices" can both land here while
            // a connect is running.
            if (_operationDepth > 0) return;

            foreach (var r in _radios) r.Dispose();
            _radios.Clear();

            var findParams = new BLUETOOTH_FIND_RADIO_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>(),
            };

            IntPtr find = BluetoothFindFirstRadio(ref findParams, out IntPtr radio);
            if (find == IntPtr.Zero) return; // no adapter, or Bluetooth switched off

            try
            {
                do
                {
                    _radios.Add(new BluetoothRadioHandle(radio));
                }
                while (BluetoothFindNextRadio(find, out radio));
            }
            finally
            {
                BluetoothFindRadioClose(find);
            }
        }
    }

    // ------------------------------------------------------------- enumeration ----

    /// <summary>
    /// Every paired ("remembered" or authenticated) device across all radios. fIssueInquiry
    /// is false so this reads the local cache only -- no 10-second discovery scan.
    /// </summary>
    public List<PairedDevice> GetPairedDevices()
    {
        var found = new List<PairedDevice>();

        lock (_gate)
        {
            foreach (var radio in _radios)
            {
                var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                    fReturnAuthenticated = true,
                    fReturnRemembered = true,
                    fReturnUnknown = false,
                    fReturnConnected = true,
                    fIssueInquiry = false,
                    cTimeoutMultiplier = 0,
                    hRadio = radio.DangerousGetHandle(),
                };

                var info = BLUETOOTH_DEVICE_INFO.Create();
                IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
                if (find == IntPtr.Zero) continue;

                try
                {
                    do
                    {
                        if (!info.fRemembered && !info.fAuthenticated) continue;

                        found.Add(new PairedDevice(
                            info.Address,
                            (info.szName ?? string.Empty).Trim(),
                            info.ulClassofDevice,
                            info.fConnected,
                            info.fAuthenticated,
                            info.fRemembered));
                    }
                    // BluetoothFindNextDevice overwrites the whole struct; only dwSize must
                    // survive, and it does, so there is nothing to reset between iterations.
                    while (BluetoothFindNextDevice(find, ref info));
                }
                finally
                {
                    BluetoothFindDeviceClose(find);
                }
            }
        }

        // A device visible through two radios shows up twice; keep the first (and prefer a
        // connected sighting, since that is the one carrying live state).
        return found
            .GroupBy(d => d.Address)
            .Select(g => g.FirstOrDefault(d => d.IsConnected) ?? g.First())
            .OrderByDescending(d => d.IsAudioDevice)
            .ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Current state of one device. This is the 2-second poll target.</summary>
    public DeviceStatus GetStatus(ulong address)
    {
        lock (_gate)
        {
            if (_radios.Count == 0) return DeviceStatus.NoRadio;

            foreach (var radio in _radios)
            {
                var info = BLUETOOTH_DEVICE_INFO.Create(address);
                uint rc = BluetoothGetDeviceInfo(radio.DangerousGetHandle(), ref info);
                if (rc != ERROR_SUCCESS) continue;

                return new DeviceStatus(
                    RadioPresent: true,
                    DeviceKnown: true,
                    IsConnected: info.fConnected,
                    IsPaired: info.fAuthenticated || info.fRemembered,
                    Name: (info.szName ?? string.Empty).Trim());
            }

            return DeviceStatus.Unknown; // radio is there, device is not paired with it
        }
    }

    // ---------------------------------------------------------------- connect ----

    /// <summary>
    /// Brings the radio link up and waits for the stack to report the device as connected.
    /// Nothing here changes persistent device state, so the audio endpoints the user already
    /// configured are the ones that come back.
    /// </summary>
    public void Connect(ulong address, ConnectOptions options, CancellationToken ct)
    {
        BeginOperation();
        try
        {
            _lastLinkError = 0;
            var (radio, info) = ResolveDevice(address);

            // One narrow exception to "never touch service state": a profile that is switched
            // off has no driver and therefore no endpoint, so no amount of link-waking will
            // produce audio. Switching a profile that is *off* back on is the same thing the
            // checkbox in the device's Bluetooth properties does, and it is self-limiting --
            // we never switch one off, so it happens at most once per profile rather than
            // spawning a new endpoint on every click.
            Guid[] missing = options.Services
                .Except(GetEnabledServices(radio, ref info))
                .ToArray();

            if (missing.Length > 0 && options.EnableMissingProfiles)
            {
                var errors = new List<string>();
                foreach (Guid service in missing)
                    SetServiceState(radio, ref info, service, BLUETOOTH_SERVICE_ENABLE, errors);

                missing = options.Services.Except(GetEnabledServices(radio, ref info)).ToArray();
            }

            WaitForLink(address, options, missing, ct);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Drops the baseband link, the way Settings' Disconnect button does. Profiles stay
    /// installed and enabled, so Windows still accepts the device's own reconnect later
    /// (AirPods going back in an ear, a headset powering on) with nothing re-created.
    /// </summary>
    public void Disconnect(ulong address, int timeoutMs, int retryIntervalMs, CancellationToken ct)
    {
        BeginOperation();
        try
        {
            ResolveDevice(address); // validates the radio, and that we are still paired

            var clock = Stopwatch.StartNew();
            long nextAttemptAt = 0;
            bool firstAttempt = true;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                DeviceStatus status = GetStatus(address);
                if (!status.RadioPresent)
                    throw new BluetoothOperationException("Bluetooth adapter disappeared mid-operation.");
                if (status.DeviceKnown && !status.IsConnected)
                    return;

                if (clock.ElapsedMilliseconds >= timeoutMs) break;

                if (clock.ElapsedMilliseconds >= nextAttemptAt)
                {
                    nextAttemptAt = clock.ElapsedMilliseconds + Math.Max(retryIntervalMs, OperationPollMs);

                    bool accepted = TryDropLink(address, out string? error);

                    // A first IOCTL rejected outright means the stack is telling us it will not
                    // do this at all; no point sitting out the whole timeout for that.
                    if (!accepted && firstAttempt && error is not null)
                        throw new BluetoothOperationException($"Disconnect was refused: {error}");

                    firstAttempt = false;
                }

                Wait(OperationPollMs, ct);
            }

            throw new BluetoothOperationException(
                "Disconnect timed out - the device is still holding the link.");
        }
        finally
        {
            EndOperation();
        }
    }

    private void BeginOperation()
    {
        lock (_gate) _operationDepth++;
    }

    private void EndOperation()
    {
        lock (_gate) _operationDepth--;
    }

    // --------------------------------------------------------------- internals ----

    private (IntPtr Radio, BLUETOOTH_DEVICE_INFO Info) ResolveDevice(ulong address)
    {
        lock (_gate)
        {
            if (_radios.Count == 0)
            {
                throw new BluetoothOperationException(
                    "No Bluetooth adapter found. Is Bluetooth turned on?");
            }

            foreach (var radio in _radios)
            {
                var info = BLUETOOTH_DEVICE_INFO.Create(address);
                if (BluetoothGetDeviceInfo(radio.DangerousGetHandle(), ref info) == ERROR_SUCCESS)
                {
                    if (!info.fRemembered && !info.fAuthenticated)
                    {
                        throw new BluetoothOperationException(
                            $"\"{info.szName}\" is no longer paired with this PC.");
                    }
                    return (radio.DangerousGetHandle(), info);
                }
            }
        }

        throw new BluetoothOperationException(
            "That device is not paired with this PC any more. Re-pair it in Windows Settings.");
    }

    /// <summary>
    /// Returns the service GUIDs currently enabled on the device. Failures are non-fatal --
    /// this only feeds the "switch a profile that is off back on" repair and the timeout
    /// message, and an empty result there costs nothing worse than a missed hint.
    /// </summary>
    private static Guid[] GetEnabledServices(IntPtr radio, ref BLUETOOTH_DEVICE_INFO info)
    {
        uint count = 0;
        uint rc = BluetoothEnumerateInstalledServices(radio, ref info, ref count, null);
        if (count == 0 || (rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA)) return Array.Empty<Guid>();

        var buffer = new Guid[count];
        rc = BluetoothEnumerateInstalledServices(radio, ref info, ref count, buffer);
        if (rc != ERROR_SUCCESS) return Array.Empty<Guid>();

        return count >= buffer.Length ? buffer : buffer.Take((int)count).ToArray();
    }

    /// <summary>
    /// Switches one profile on. Only ever called with BLUETOOTH_SERVICE_ENABLE, and only for a
    /// profile that is currently off. ERROR_SERVICE_DOES_NOT_EXIST is expected and ignored: it
    /// means the device does not carry that profile (plenty of speakers have no Hands-Free).
    /// </summary>
    private static bool SetServiceState(
        IntPtr radio, ref BLUETOOTH_DEVICE_INFO info, Guid service, uint flag, List<string> errors)
    {
        Guid local = service; // BluetoothSetServiceState takes the GUID by reference
        uint rc = BluetoothSetServiceState(radio, ref info, ref local, flag);

        if (rc == ERROR_SUCCESS) return true;
        if (rc == ERROR_SERVICE_DOES_NOT_EXIST) return false;

        errors.Add($"{BluetoothProfiles.Describe(service)}: {BluetoothOperationException.DescribeWin32((int)rc)}");
        return false;
    }

    /// <summary>
    /// Polls fConnected while re-issuing the link attempt every LinkRetryIntervalMs. Throws
    /// with a state-appropriate message on timeout.
    /// </summary>
    private void WaitForLink(
        ulong address, ConnectOptions options, Guid[] stillDisabled, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long nextAttemptAt = 0;
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            DeviceStatus status = GetStatus(address);
            if (!status.RadioPresent)
                throw new BluetoothOperationException("Bluetooth adapter disappeared mid-operation.");
            if (status.DeviceKnown && status.IsConnected)
                return;

            if (clock.ElapsedMilliseconds >= options.ConnectTimeoutMs) break;

            if (clock.ElapsedMilliseconds >= nextAttemptAt)
            {
                nextAttemptAt = clock.ElapsedMilliseconds
                    + Math.Max(options.LinkRetryIntervalMs, OperationPollMs);
                StartLinkAttempt(address, options.Services, attempt++);
            }

            Wait(OperationPollMs, ct);
        }

        if (stillDisabled.Length > 0)
        {
            string names = string.Join(", ", stillDisabled.Select(BluetoothProfiles.Describe));
            throw new BluetoothOperationException(
                $"Connect timed out. {names} is switched off for this device in Windows, so it has no audio endpoint - turn it on under the device's Bluetooth properties, or tick \"Switch on audio profiles that are off\" in Settings.");
        }

        int linkError = _lastLinkError;
        throw new BluetoothOperationException(linkError == 0
            ? "Connect timed out. Are the AirPods out of the case and charged?"
            : $"Connect timed out: {BluetoothOperationException.DescribeWinsock(linkError)}.");
    }

    /// <summary>
    /// Fire-and-forget RFCOMM connection attempt -- the whole of our "connect". We never await
    /// it: connect() on AF_BTH can block for many seconds and its *return value is irrelevant*,
    /// because the useful side effect (the ACL link coming up so SDP can run) has already
    /// happened by the time it resolves either way. Attempts alternate between the configured
    /// service classes, since a device that ignores one sometimes answers the other.
    /// </summary>
    private void StartLinkAttempt(ulong address, Guid[] services, int attempt)
    {
        Guid target = PickTargetService(services, attempt);

        _ = Task.Run(() =>
        {
            IntPtr socketHandle = INVALID_SOCKET;
            try
            {
                EnsureWinsock();

                socketHandle = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
                if (socketHandle == INVALID_SOCKET)
                {
                    _lastLinkError = Marshal.GetLastWin32Error();
                    return;
                }

                var addr = new SOCKADDR_BTH
                {
                    addressFamily = AF_BTH,
                    btAddr = address,
                    serviceClassId = target,
                    // port 0 + a service GUID tells Winsock to resolve the RFCOMM channel
                    // over SDP itself, which is the part that wakes the remote device.
                    port = 0,
                };

                int rc = connect(socketHandle, ref addr, Marshal.SizeOf<SOCKADDR_BTH>());
                _lastLinkError = rc == 0 ? 0 : Marshal.GetLastWin32Error();
            }
            catch
            {
                // Best effort only.
            }
            finally
            {
                if (socketHandle != INVALID_SOCKET) closesocket(socketHandle);
            }
        });
    }

    /// <summary>
    /// Hands-Free first -- it is a real RFCOMM service, so SDP resolves an actual channel and
    /// the link tends to come up fastest. Later attempts cycle through the configured list.
    /// </summary>
    private static Guid PickTargetService(Guid[] services, int attempt)
    {
        Guid[] usable = services.Where(s => s != Guid.Empty).ToArray();
        if (usable.Length == 0) return BluetoothProfiles.Handsfree;

        if (attempt == 0 && usable.Contains(BluetoothProfiles.Handsfree))
            return BluetoothProfiles.Handsfree;

        return usable[attempt % usable.Length];
    }

    /// <summary>
    /// IOCTL_BTH_DISCONNECT_DEVICE against every open radio. Returns true as soon as one of
    /// them accepts the request, or tells us the device was not connected in the first place;
    /// <paramref name="error"/> carries the first real failure otherwise.
    /// </summary>
    private bool TryDropLink(ulong address, out string? error)
    {
        error = null;
        int? firstFailure = null;

        lock (_gate)
        {
            foreach (var radio in _radios)
            {
                ulong target = address; // BTH_ADDR, passed by reference as the input buffer

                if (DeviceIoControl(
                        radio.DangerousGetHandle(), IOCTL_BTH_DISCONNECT_DEVICE,
                        ref target, sizeof(ulong), IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    return true;
                }

                int rc = Marshal.GetLastWin32Error();

                // "Nothing to disconnect" counts as success here; the poll will see fConnected
                // go false, or it already is.
                if (rc is ERROR_NOT_FOUND or ERROR_DEVICE_NOT_CONNECTED or ERROR_NO_MORE_ITEMS)
                    return true;

                firstFailure ??= rc;
            }
        }

        if (firstFailure is int code) error = BluetoothOperationException.DescribeWin32(code);
        return false;
    }

    private void EnsureWinsock()
    {
        lock (_gate)
        {
            if (_winsockReady) return;
            WSAStartup(0x0202, new byte[512]); // MAKEWORD(2,2)
            _winsockReady = true;
        }
    }

    private static void Wait(int milliseconds, CancellationToken ct)
    {
        if (milliseconds <= 0) return;
        if (ct.WaitHandle.WaitOne(milliseconds)) ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var r in _radios) r.Dispose();
            _radios.Clear();
        }
    }
}
