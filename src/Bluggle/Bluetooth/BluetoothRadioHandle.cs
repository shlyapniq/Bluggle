using Microsoft.Win32.SafeHandles;

namespace Bluggle.Bluetooth;

/// <summary>
/// Owns a local radio handle returned by BluetoothFindFirstRadio / BluetoothFindNextRadio.
/// Those handles are ordinary kernel handles and must be released with CloseHandle -- the
/// Find*Close functions only release the *enumerator*, not the handles it produced.
/// </summary>
internal sealed class BluetoothRadioHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public BluetoothRadioHandle(IntPtr handle) : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
