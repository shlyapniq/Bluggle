using System.ComponentModel;

namespace Bluggle.Bluetooth;

/// <summary>
/// A Bluetooth operation that failed in a way worth showing the user. Message is already
/// human-readable and short enough for a tooltip.
/// </summary>
public sealed class BluetoothOperationException : Exception
{
    public BluetoothOperationException(string message, int win32Code = 0) : base(message)
    {
        Win32Code = win32Code;
    }

    public int Win32Code { get; }

    /// <summary>
    /// Turns a Win32 error from the Bluetooth stack into something that fits in a tooltip.
    /// The generic Win32Exception text for these codes is unhelpful ("The service does not
    /// exist" tells you nothing about AirPods), so the interesting ones get their own wording.
    /// </summary>
    public static string DescribeWin32(int code) => code switch
    {
        NativeMethods.ERROR_SUCCESS => "OK",
        NativeMethods.ERROR_INVALID_PARAMETER =>
            "Bluetooth stack rejected the request (invalid parameter).",
        NativeMethods.ERROR_SERVICE_DOES_NOT_EXIST =>
            "Device does not advertise that audio profile.",
        NativeMethods.ERROR_DEVICE_NOT_CONNECTED =>
            "Device is not reachable - in the case, or out of range?",
        NativeMethods.ERROR_NOT_FOUND =>
            "Device is no longer paired with this PC.",
        NativeMethods.ERROR_TIMEOUT =>
            "Bluetooth stack timed out talking to the device.",
        _ => SafeSystemMessage(code),
    };

    /// <summary>
    /// Winsock errors from the AF_BTH connect that wakes the link. Worth spelling out, because
    /// they say something specific about where the attempt died: 10049 means the sockaddr was
    /// malformed and the radio was never touched, 10061 means the link came up and the remote
    /// merely declined that channel, and 10064 means the device is simply not listening.
    /// </summary>
    public static string DescribeWinsock(int code) => code switch
    {
        0 => "OK",
        10013 => "access denied (10013)",
        10049 => "the address was rejected before the radio was used (10049)",
        10060 => "the device never answered (10060)",
        10061 => "the device refused the channel (10061)",
        10064 => "the device is not listening - in the case, or asleep? (10064)",
        10065 => "the device is unreachable (10065)",
        10108 => "no such service on the device (10108)",
        _ => $"Winsock error {code}",
    };

    private static string SafeSystemMessage(int code)
    {
        try
        {
            return $"{new Win32Exception(code).Message} (0x{code:X})";
        }
        catch
        {
            return $"Bluetooth error 0x{code:X}.";
        }
    }
}
