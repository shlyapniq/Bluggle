namespace Bluggle.Bluetooth;

/// <summary>
/// Bluetooth SIG service-class UUIDs, in the standard base form
/// 0000xxxx-0000-1000-8000-00805F9B34FB.
/// </summary>
public static class BluetoothProfiles
{
    /// <summary>A2DP Sink (0x110B) -- stereo audio playback. The one that matters for music.</summary>
    public static readonly Guid AudioSink = new("0000110b-0000-1000-8000-00805f9b34fb");

    /// <summary>A2DP Source (0x110A).</summary>
    public static readonly Guid AudioSource = new("0000110a-0000-1000-8000-00805f9b34fb");

    /// <summary>Advanced Audio Distribution profile UUID (0x110D).</summary>
    public static readonly Guid AdvancedAudioDistribution = new("0000110d-0000-1000-8000-00805f9b34fb");

    /// <summary>Hands-Free (0x111E) -- the mic / call path. AirPods need this toggled too.</summary>
    public static readonly Guid Handsfree = new("0000111e-0000-1000-8000-00805f9b34fb");

    /// <summary>Headset (0x1108) -- the older HSP profile, still present on some devices.</summary>
    public static readonly Guid Headset = new("00001108-0000-1000-8000-00805f9b34fb");

    /// <summary>AV Remote Control (0x110E) -- play/pause/skip passthrough.</summary>
    public static readonly Guid AvRemoteControl = new("0000110e-0000-1000-8000-00805f9b34fb");

    /// <summary>
    /// What we toggle by default. A2DP Sink alone connects audio but leaves the mic dead and,
    /// on AirPods specifically, often produces a link that drops within seconds; toggling HFP
    /// alongside it is what makes the connection stick.
    /// </summary>
    public static readonly Guid[] DefaultAudioServices = { AudioSink, Handsfree };

    /// <summary>Friendly names for the settings UI and log lines.</summary>
    public static string Describe(Guid service)
    {
        if (service == AudioSink) return "A2DP Sink";
        if (service == AudioSource) return "A2DP Source";
        if (service == AdvancedAudioDistribution) return "A2DP";
        if (service == Handsfree) return "Hands-Free";
        if (service == Headset) return "Headset";
        if (service == AvRemoteControl) return "AVRCP";
        return service.ToString();
    }
}
