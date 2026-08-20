using System.Runtime.InteropServices;

namespace Bluggle.Bluetooth;

/// <summary>
/// P/Invoke surface for the Microsoft Bluetooth stack, Winsock AF_BTH sockets, and the
/// handful of user32 entry points we need for physical-pixel window placement.
///
/// Note on the DLL name: the public docs talk about "BluetoothAPIs.dll", but that is only
/// the name of the *import library* (BluetoothAPIs.lib). The DLL that actually exports
/// these symbols on Vista+ is bthprops.cpl -- a DLL despite the extension. Importing
/// "BluetoothAPIs.dll" happens to work on some builds and fails on others; bthprops.cpl
/// is correct everywhere from Win7 through Win11.
/// </summary>
internal static class NativeMethods
{
    private const string BthProps = "bthprops.cpl";

    // ---------------------------------------------------------------- error codes --

    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    internal const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    internal const int ERROR_NOT_FOUND = 1168;
    internal const int ERROR_TIMEOUT = 1460;

    // ------------------------------------------------------------ service toggling --

    internal const uint BLUETOOTH_SERVICE_DISABLE = 0x00;
    internal const uint BLUETOOTH_SERVICE_ENABLE = 0x01;

    internal const int BLUETOOTH_MAX_NAME_SIZE = 248;

    // ------------------------------------------------------------------- structs ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    /// <summary>
    /// BLUETOOTH_DEVICE_INFO. dwSize must be set to sizeof(struct) before every call: the
    /// stack uses it as a version tag and fails the call outright otherwise. We always
    /// compute it with Marshal.SizeOf rather than hardcoding 560, so this stays correct if
    /// the struct is ever marshalled differently.
    ///
    /// Address is the union BLUETOOTH_ADDRESS { ULONGLONG ullLong; BYTE rgBytes[6]; } where
    /// rgBytes[0] is the LEAST significant byte. The human-readable MAC AA:BB:CC:DD:EE:FF is
    /// therefore the ulong printed big-endian from bits 40 down to 0 -- see
    /// PairedDevice.FormatAddress.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = BLUETOOTH_MAX_NAME_SIZE)]
        public string szName;

        internal static BLUETOOTH_DEVICE_INFO Create(ulong address = 0) => new()
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
            Address = address,
            szName = string.Empty,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    // ------------------------------------------------------------------ bthprops ----

    [DllImport(BthProps, SetLastError = true)]
    internal static extern IntPtr BluetoothFindFirstRadio(
        ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport(BthProps, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr phRadio);

    [DllImport(BthProps, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport(BthProps, SetLastError = true)]
    internal static extern IntPtr BluetoothFindFirstDevice(
        ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport(BthProps, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport(BthProps, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    /// <summary>
    /// Refreshes a BLUETOOTH_DEVICE_INFO from the local cache; Address must be pre-filled.
    /// This is a local lookup with no radio traffic, which is why polling it every 2s costs
    /// nothing and is our primary connection-state source.
    /// </summary>
    [DllImport(BthProps, SetLastError = true)]
    internal static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

    /// <summary>
    /// Lists the service GUIDs currently ENABLED on a device -- not the ones it is capable
    /// of. Call once with pGuidServices = null to get the count in pcServiceInout (returns
    /// ERROR_MORE_DATA), then again with a right-sized array.
    /// </summary>
    [DllImport(BthProps, SetLastError = true)]
    internal static extern uint BluetoothEnumerateInstalledServices(
        IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi,
        ref uint pcServiceInout, [In, Out] Guid[]? pGuidServices);

    /// <summary>
    /// Enables or disables one service (profile) on one paired device. DISABLE is destructive
    /// in a way the name does not suggest -- it uninstalls the profile's device node, taking
    /// the audio endpoint and all of its per-endpoint settings with it -- so this app only ever
    /// calls it with BLUETOOTH_SERVICE_ENABLE, to switch a profile that is off back on.
    /// </summary>
    [DllImport(BthProps, SetLastError = true)]
    internal static extern uint BluetoothSetServiceState(
        IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

    // ------------------------------------------------------------- radio IOCTLs ----
    //
    // bthioctl.h in the WDK, reachable from user mode through the radio handle that
    // BluetoothFindFirstRadio hands out (that handle is an ordinary CreateFile handle on the
    // radio's device interface). IOCTL_BTH_DISCONNECT_DEVICE takes a BTH_ADDR and drops the
    // baseband link to it, which is precisely what the Disconnect button in Windows' Bluetooth
    // settings does: the profiles stay installed, only the connection goes away.

    private const uint FILE_DEVICE_BLUETOOTH = 0x00000041;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    /// <summary>CTL_CODE(FILE_DEVICE_BLUETOOTH, id, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    private static uint BthCtl(uint id) =>
        (FILE_DEVICE_BLUETOOTH << 16) | (FILE_ANY_ACCESS << 14) | (id << 2) | METHOD_BUFFERED;

    /// <summary>BTH_CTL(0x03) == 0x41000C. Input buffer is a bare BTH_ADDR (ULONGLONG).</summary>
    internal static readonly uint IOCTL_BTH_DISCONNECT_DEVICE = BthCtl(0x03);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        ref ulong lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    // ------------------------------------------------------- AF_BTH socket wake-up ----
    //
    // Opening an RFCOMM socket to a device makes Winsock run an SDP lookup, and SDP needs a
    // baseband/ACL link, so the link comes up first. Even when the remote end then refuses the
    // RFCOMM channel, that link is what wakes a sleepy A2DP sink and lets the already-installed
    // profile drivers attach. This is how Connect() connects.

    internal const int AF_BTH = 32;
    internal const int SOCK_STREAM = 1;
    internal const int BTHPROTO_RFCOMM = 3;
    internal static readonly IntPtr INVALID_SOCKET = new(-1);

    /// <summary>
    /// SOCKADDR_BTH. The layout is the whole ballgame: ws2bth.h declares this struct inside
    /// pshpack1.h, so it is packed to 1-byte alignment and is 30 bytes, NOT the 40 bytes
    /// natural alignment would give you. Get it wrong and btAddr is read from offset 8 instead
    /// of 2, so Winsock sees a garbage address and connect() fails instantly with
    /// WSAEADDRNOTAVAIL (10049) having never gone near the radio -- a silent no-op that looks
    /// exactly like a device refusing to wake up.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 30, Pack = 1)]
    internal struct SOCKADDR_BTH
    {
        [FieldOffset(0)] public ushort addressFamily;
        [FieldOffset(2)] public ulong btAddr;
        [FieldOffset(10)] public Guid serviceClassId;
        [FieldOffset(26)] public uint port;
    }

    // WSADATA is 400+ bytes of fixed arrays we never read; a scratch buffer avoids
    // declaring the struct at all.
    [DllImport("ws2_32.dll", EntryPoint = "WSAStartup")]
    internal static extern int WSAStartup(ushort wVersionRequested, [Out] byte[] lpWSAData);

    [DllImport("ws2_32.dll", SetLastError = true)]
    internal static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    internal static extern int connect(IntPtr s, ref SOCKADDR_BTH name, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    internal static extern int closesocket(IntPtr s);

    // -------------------------------------------------------------------- user32 ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    internal const uint MONITOR_DEFAULTTONULL = 0x00000000;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    // GetWindowLongPtrW only exists on 64-bit user32; on 32-bit it is a macro for
    // GetWindowLongW. Branch on pointer size so the same source works either way.
    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
