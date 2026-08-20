using System.Globalization;

namespace Bluggle.Bluetooth;

/// <summary>A paired Bluetooth device as reported by the local stack.</summary>
public sealed record PairedDevice(
    ulong Address,
    string Name,
    uint ClassOfDevice,
    bool IsConnected,
    bool IsAuthenticated,
    bool IsRemembered)
{
    /// <summary>Canonical MAC text, e.g. "A0:B1:C2:D3:E4:F5". Used as the config key.</summary>
    public string AddressText => FormatAddress(Address);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? AddressText : Name;

    // Class of Device layout (Bluetooth SIG "Baseband" assigned numbers):
    //   bits  2..7   minor device class
    //   bits  8..12  major device class   (0x04 = Audio/Video)
    //   bits 13..23  major service classes (bit 21 = Audio)
    private const uint MajorClassMask = 0x1F00;
    private const uint MajorClassAudioVideo = 0x0400;
    private const uint ServiceClassAudio = 0x200000;

    /// <summary>
    /// True for headsets, speakers, earbuds and the like. AirPods report 0x240404:
    /// major device class 0x04 (Audio/Video), minor 0x01 (wearable headset), audio service bit set.
    /// </summary>
    public bool IsAudioDevice =>
        (ClassOfDevice & MajorClassMask) == MajorClassAudioVideo ||
        (ClassOfDevice & ServiceClassAudio) != 0;

    /// <summary>
    /// BLUETOOTH_ADDRESS.rgBytes[0] is the least significant byte, so the conventional
    /// display order is most-significant first: bits 40..0 in 8-bit steps.
    /// </summary>
    public static string FormatAddress(ulong address)
    {
        var bytes = new string[6];
        for (int i = 0; i < 6; i++)
        {
            int shift = (5 - i) * 8;
            bytes[i] = ((address >> shift) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
        }
        return string.Join(":", bytes);
    }

    /// <summary>
    /// Parses "A0:B1:C2:D3:E4:F5", "A0-B1-...", or bare "A0B1C2D3E4F5". Tolerant on purpose:
    /// the value round-trips through a hand-editable JSON config file.
    /// </summary>
    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        Span<char> hex = stackalloc char[12];
        int n = 0;
        foreach (char c in text)
        {
            if (c is ':' or '-' or '.' or ' ') continue;
            if (!Uri.IsHexDigit(c)) return false;
            if (n >= 12) return false;
            hex[n++] = c;
        }
        if (n != 12) return false;

        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}

/// <summary>Point-in-time view of one device, refreshed by the poll timer.</summary>
public readonly record struct DeviceStatus(
    bool RadioPresent,
    bool DeviceKnown,
    bool IsConnected,
    bool IsPaired,
    string Name)
{
    public static DeviceStatus NoRadio => new(false, false, false, false, string.Empty);

    public static DeviceStatus Unknown => new(true, false, false, false, string.Empty);
}
