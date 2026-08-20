using System.Windows.Threading;
using Bluggle.Bluetooth;
using Bluggle.Services;

namespace Bluggle;

public enum WidgetState
{
    /// <summary>No Bluetooth adapter, or the radio is switched off.</summary>
    Unavailable,

    /// <summary>Adapter is fine but no target device has been chosen yet.</summary>
    NoDevice,

    Disconnected,
    Connecting,
    Connected,
    Disconnecting,

    /// <summary>Transient: the last operation failed. Clears itself after a few seconds.</summary>
    Error,
}

/// <summary>
/// Owns the poll loop and the connect/disconnect state machine.
///
/// Threading: this object is created on the UI thread and every member is touched from it.
/// The blocking Bluetooth calls go through Task.Run and are awaited, so continuations land
/// back on the WPF dispatcher and no marshalling is needed anywhere in the class.
/// </summary>
public sealed class WidgetController : IDisposable
{
    private readonly BluetoothAudioController _bluetooth = new();
    private readonly ConnectSound _sound = new();
    private readonly ConfigStore _store;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _errorTimer;
    private readonly DispatcherTimer _soundTimer;

    private CancellationTokenSource? _operationCts;
    private bool _operationInFlight;
    private int _tick;
    private int _consecutiveMisses;
    private bool _disposed;

    /// <summary>Re-enumerate the paired device list every this many poll ticks (~20 s).</summary>
    private const int DeviceRefreshEveryTicks = 10;

    public WidgetController(ConfigStore store)
    {
        _store = store;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Config.PollIntervalMs),
        };
        _pollTimer.Tick += OnPollTick;

        _soundTimer = new DispatcherTimer();
        _soundTimer.Tick += (_, _) =>
        {
            _soundTimer.Stop();

            // Only chime if the connection is still standing. Disconnecting again inside the
            // delay window should not be rewarded with a "you are connected" noise.
            if (!_disposed && State is WidgetState.Connected) _sound.Play();
        };

        _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _errorTimer.Tick += (_, _) =>
        {
            _errorTimer.Stop();
            ErrorMessage = null;
            _ = PollOnceAsync();
        };
    }

    // -------------------------------------------------------------------- state ----

    public AppConfig Config => _store.Config;

    public ConfigStore Store => _store;

    public WidgetState State { get; private set; } = WidgetState.NoDevice;

    /// <summary>Non-null only while an error is being shown.</summary>
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<PairedDevice> Devices { get; private set; } = Array.Empty<PairedDevice>();

    /// <summary>Raised whenever State, ErrorMessage or Devices changed. Always on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Tooltip text describing the current situation.</summary>
    public string StatusText
    {
        get
        {
            if (ErrorMessage is not null) return ErrorMessage;

            string name = Config.DeviceName is { Length: > 0 } n ? n : "device";
            return State switch
            {
                WidgetState.Unavailable => "Bluetooth is off or no adapter was found.",
                WidgetState.NoDevice => "Right-click to pick a Bluetooth audio device.",
                WidgetState.Connected => $"{name} - connected. Click to disconnect.",
                WidgetState.Disconnected => $"{name} - disconnected. Click to connect.",
                WidgetState.Connecting => $"Connecting to {name}...",
                WidgetState.Disconnecting => $"Disconnecting {name}...",
                _ => name,
            };
        }
    }

    // ------------------------------------------------------------------ lifetime ----

    public async Task StartAsync()
    {
        StartupManager.RefreshPathIfStale();

        await Task.Run(() => _bluetooth.RefreshRadios());
        await RefreshDevicesAsync();
        await PollOnceAsync();

        _pollTimer.Start();
    }

    // -------------------------------------------------------------------- input ----

    /// <summary>
    /// Left click. Connects when disconnected, disconnects when connected. Ignored while an
    /// operation is already running - the alternative (cancel and restart) tends to leave the
    /// stack half-way through a profile change.
    /// </summary>
    public async Task ToggleAsync()
    {
        if (_operationInFlight) return;

        if (!Config.HasDevice)
        {
            ShowError("No device selected - right-click to pick one.");
            return;
        }

        ulong address = Config.DeviceAddressValue;
        DeviceStatus status = await Task.Run(() =>
        {
            if (!_bluetooth.HasRadio) _bluetooth.RefreshRadios();
            return _bluetooth.GetStatus(address);
        });

        if (!status.RadioPresent)
        {
            SetState(WidgetState.Unavailable);
            ShowError("Bluetooth is off or no adapter was found.");
            return;
        }

        if (!status.DeviceKnown || !status.IsPaired)
        {
            ShowError($"{Config.DeviceName ?? "That device"} is not paired any more.");
            return;
        }

        bool connecting = !status.IsConnected;
        await RunOperationAsync(connecting);
    }

    private async Task RunOperationAsync(bool connecting)
    {
        ulong address = Config.DeviceAddressValue;
        ConnectOptions options = Config.ToConnectOptions();
        int disconnectTimeout = Config.DisconnectTimeoutMs;
        int retryInterval = Config.LinkRetryIntervalMs;

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        CancellationToken token = _operationCts.Token;

        _operationInFlight = true;
        ErrorMessage = null;
        SetState(connecting ? WidgetState.Connecting : WidgetState.Disconnecting);

        try
        {
            await Task.Run(() =>
            {
                if (connecting) _bluetooth.Connect(address, options, token);
                else _bluetooth.Disconnect(address, disconnectTimeout, retryInterval, token);
            }, token);

            SetState(connecting ? WidgetState.Connected : WidgetState.Disconnected);

            // Only on the way in, and only for a connect the user asked for: the whole point is
            // to confirm the click landed without having to look at the widget.
            if (connecting && Config.PlaySoundOnConnect) ScheduleChime();
        }
        catch (BluetoothOperationException ex)
        {
            ShowError(ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; leave the state alone.
        }
        catch (Exception ex)
        {
            ShowError($"Unexpected Bluetooth failure: {ex.Message}");
        }
        finally
        {
            _operationInFlight = false;
        }

        // Whatever happened, let the poll decide the truth on the next tick.
        if (State is not WidgetState.Error) await PollOnceAsync();
    }

    /// <summary>
    /// Queues the confirmation chime for SoundDelayMs from now, restarting the wait if one was
    /// already pending. The delay is what makes the chime audible at all: it gives Windows time
    /// to finish building the audio endpoint and switch the default output over to it.
    /// </summary>
    private void ScheduleChime()
    {
        _soundTimer.Stop();

        int delay = Math.Max(Config.SoundDelayMs, 0);
        if (delay == 0)
        {
            _sound.Play();
            return;
        }

        _soundTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _soundTimer.Start();
    }

    public void SelectDevice(PairedDevice device)
    {
        Config.DeviceAddress = device.AddressText;
        Config.DeviceName = device.DisplayName;
        _store.SaveNow();

        ErrorMessage = null;
        _ = PollOnceAsync();
    }

    public async Task RefreshDevicesAsync()
    {
        List<PairedDevice> devices = await Task.Run(() =>
        {
            if (!_bluetooth.HasRadio) _bluetooth.RefreshRadios();
            return _bluetooth.GetPairedDevices();
        });

        Devices = devices;

        // Keep the stored friendly name fresh - devices get renamed in Windows Settings.
        if (Config.HasDevice)
        {
            PairedDevice? match = devices.FirstOrDefault(d => d.Address == Config.DeviceAddressValue);
            if (match is not null && match.DisplayName != Config.DeviceName)
            {
                Config.DeviceName = match.DisplayName;
                _store.SaveDebounced();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // --------------------------------------------------------------------- poll ----

    private async void OnPollTick(object? sender, EventArgs e)
    {
        _tick++;

        if (_tick % DeviceRefreshEveryTicks == 0)
        {
            try { await RefreshDevicesAsync(); }
            catch { /* enumeration is best-effort */ }
        }

        await PollOnceAsync();
    }

    /// <summary>
    /// Reads the real connection state and folds it into the widget state. Deliberately does
    /// nothing while an operation is running or an error is on screen, so a transient icon or
    /// an error flash is never overwritten mid-animation.
    /// </summary>
    private async Task PollOnceAsync()
    {
        if (_disposed || _operationInFlight || ErrorMessage is not null) return;

        if (!Config.HasDevice)
        {
            SetState(WidgetState.NoDevice);
            return;
        }

        ulong address = Config.DeviceAddressValue;

        DeviceStatus status;
        try
        {
            status = await Task.Run(() =>
            {
                // Radio handles go stale when the user toggles Bluetooth off and on again,
                // so reopen them whenever we have none, and occasionally when the device has
                // gone missing for a while (which looks the same as a stale handle).
                if (!_bluetooth.HasRadio) _bluetooth.RefreshRadios();

                DeviceStatus first = _bluetooth.GetStatus(address);
                if (first.RadioPresent && !first.DeviceKnown && _consecutiveMisses >= 4)
                {
                    _bluetooth.RefreshRadios();
                    return _bluetooth.GetStatus(address);
                }
                return first;
            });
        }
        catch
        {
            SetState(WidgetState.Unavailable);
            return;
        }

        if (_disposed || _operationInFlight || ErrorMessage is not null) return;

        if (!status.RadioPresent)
        {
            _consecutiveMisses = 0;
            SetState(WidgetState.Unavailable);
            return;
        }

        if (!status.DeviceKnown)
        {
            _consecutiveMisses++;
            SetState(WidgetState.Disconnected);
            return;
        }

        _consecutiveMisses = 0;
        SetState(status.IsConnected ? WidgetState.Connected : WidgetState.Disconnected);
    }

    // ------------------------------------------------------------------- helpers ----

    private void SetState(WidgetState state)
    {
        if (State == state && ErrorMessage is null) return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Surfaces a failure without stealing focus: the widget flashes red and the text lands in
    /// the tooltip. No message box, ever - a modal dialog from a background poll would yank
    /// focus out of whatever the user was typing in.
    /// </summary>
    public void ShowError(string message)
    {
        ErrorMessage = message;
        State = WidgetState.Error;
        Changed?.Invoke(this, EventArgs.Empty);

        _errorTimer.Stop();
        _errorTimer.Start();
    }

    /// <summary>Applies a changed poll interval without a restart.</summary>
    public void ApplyConfigChanges()
    {
        _pollTimer.Interval = TimeSpan.FromMilliseconds(Config.PollIntervalMs);
        _ = PollOnceAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _errorTimer.Stop();
        _soundTimer.Stop();
        _operationCts?.Cancel();

        // Nothing to undo on the way out: disconnecting only drops the link, it never switches
        // a profile off, so the device is free to auto-connect on its own whether or not the
        // widget is running.
        _operationCts?.Dispose();
        _sound.Dispose();
        _bluetooth.Dispose();
    }
}
