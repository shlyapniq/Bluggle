using System.Text.Json.Serialization;
using Bluggle.Bluetooth;

namespace Bluggle.Services;

/// <summary>
/// Everything persisted to %APPDATA%\Bluggle\config.json. Written to be hand-editable:
/// plain names, plain values, no magic numbers you cannot guess the meaning of.
/// </summary>
public sealed class AppConfig
{
    // ------------------------------------------------------------------ window ----

    /// <summary>Widget position in PHYSICAL pixels on the virtual desktop. Null = first run.</summary>
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    /// <summary>Edge length of the widget in device-independent pixels.</summary>
    public double WidgetSize { get; set; } = 64;

    /// <summary>Window opacity while disconnected and not hovered.</summary>
    public double IdleOpacity { get; set; } = 0.6;

    /// <summary>Accent colour used for the connected state, as #RRGGBB or #AARRGGBB.</summary>
    public string AccentColor { get; set; } = "#4CC38A";

    /// <summary>
    /// Adds WS_EX_NOACTIVATE so clicking the widget never pulls focus off your current app.
    /// Off by default because it makes the right-click menu less well-behaved on some systems.
    /// </summary>
    public bool NoActivate { get; set; }

    // ------------------------------------------------------------------ device ----

    /// <summary>Target device MAC, e.g. "A0:B1:C2:D3:E4:F5".</summary>
    public string? DeviceAddress { get; set; }

    /// <summary>Last known friendly name. Display only - DeviceAddress is the identity.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Show non-audio paired devices in the right-click menu too.</summary>
    public bool ShowAllPairedDevices { get; set; }

    // --------------------------------------------------------------- behaviour ----

    public bool StartWithWindows { get; set; }

    /// <summary>Connection-state poll period. 2s is plenty; the call is a local cache read.</summary>
    public int PollIntervalMs { get; set; } = 2000;

    public int ConnectTimeoutMs { get; set; } = 15000;

    public int DisconnectTimeoutMs { get; set; } = 8000;

    /// <summary>
    /// How often a connect re-pokes the device, and a disconnect re-issues the drop, while
    /// waiting for the state to change. A device waking from deep sleep routinely ignores the
    /// first attempt.
    /// </summary>
    public int LinkRetryIntervalMs { get; set; } = 2500;

    /// <summary>
    /// If one of the profiles below is switched *off* for the device in Windows, switch it back
    /// on before connecting. That profile has no audio endpoint while it is off, so a connect
    /// could otherwise never produce sound. Nothing is ever switched off again, so this fires at
    /// most once per profile -- unlike the old disable/enable cycle, which rebuilt the endpoints
    /// on every single click. Turn it off if you deliberately disabled a profile (a common way
    /// to be rid of the awful Hands-Free mic endpoint) and want it left alone.
    /// </summary>
    public bool EnableMissingProfilesOnConnect { get; set; } = true;

    /// <summary>
    /// Play the bundled chime once a connect you asked for has succeeded. The widget is small
    /// and off to one side, so a sound is often the first you know of it. Connections the
    /// device makes on its own (AirPods coming out of the case) stay silent.
    /// </summary>
    public bool PlaySoundOnConnect { get; set; } = true;

    /// <summary>
    /// How long to wait after the link comes up before playing the chime. This is not padding
    /// for effect: the connection is reported the moment the radio link is up, but Windows
    /// takes a few seconds more to finish building the audio endpoint and make it the default
    /// output. Chime immediately and it plays out of whatever device was default before -- your
    /// speakers, or nothing at all -- which sounds exactly like a chime that never fired.
    /// </summary>
    public int SoundDelayMs { get; set; } = 3000;

    /// <summary>
    /// Service UUIDs this app cares about: what it wakes on connect, and what it expects to be
    /// switched on. Defaults to A2DP Sink + Hands-Free. Add
    /// "0000110e-0000-1000-8000-00805f9b34fb" (AVRCP) if media keys misbehave.
    /// </summary>
    public List<string> ServiceGuids { get; set; } = new()
    {
        BluetoothProfilesText.AudioSink,
        BluetoothProfilesText.Handsfree,
    };

    // ------------------------------------------------------------------ helpers ----

    [JsonIgnore]
    public ulong DeviceAddressValue =>
        PairedDevice.TryParseAddress(DeviceAddress, out ulong value) ? value : 0;

    [JsonIgnore]
    public bool HasDevice => DeviceAddressValue != 0;

    /// <summary>Parsed service list, falling back to the defaults if the file was mangled.</summary>
    public Guid[] ResolveServices()
    {
        var parsed = new List<Guid>();
        foreach (string text in ServiceGuids)
        {
            if (Guid.TryParse(text, out Guid guid) && guid != Guid.Empty) parsed.Add(guid);
        }
        return parsed.Count > 0 ? parsed.ToArray() : BluetoothProfiles.DefaultAudioServices;
    }

    public ConnectOptions ToConnectOptions() => new(
        ResolveServices(),
        ConnectTimeoutMs,
        LinkRetryIntervalMs,
        EnableMissingProfilesOnConnect);

    /// <summary>Pulls obviously broken values back into range after a hand edit.</summary>
    public void Normalize()
    {
        WidgetSize = Math.Clamp(WidgetSize, 32, 256);
        IdleOpacity = Math.Clamp(IdleOpacity, 0.15, 1.0);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 500, 30000);
        ConnectTimeoutMs = Math.Clamp(ConnectTimeoutMs, 3000, 60000);
        DisconnectTimeoutMs = Math.Clamp(DisconnectTimeoutMs, 1000, 60000);
        LinkRetryIntervalMs = Math.Clamp(LinkRetryIntervalMs, 500, 15000);
        SoundDelayMs = Math.Clamp(SoundDelayMs, 0, 30000);
        ServiceGuids ??= new List<string>();
        if (ServiceGuids.Count == 0)
        {
            ServiceGuids.Add(BluetoothProfilesText.AudioSink);
            ServiceGuids.Add(BluetoothProfilesText.Handsfree);
        }
    }
}

/// <summary>String forms of the profile UUIDs, for the JSON defaults.</summary>
public static class BluetoothProfilesText
{
    public const string AudioSink = "0000110b-0000-1000-8000-00805f9b34fb";
    public const string Handsfree = "0000111e-0000-1000-8000-00805f9b34fb";
    public const string AvRemoteControl = "0000110e-0000-1000-8000-00805f9b34fb";
}
