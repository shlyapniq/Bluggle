using System.Runtime.InteropServices;
using Bluggle.Bluetooth;
using static Bluggle.Bluetooth.NativeMethods;

namespace Bluggle.Services;

/// <summary>
/// Keeps the widget on a monitor that actually exists.
///
/// All of the arithmetic here is in physical pixels, which is the whole reason the app places
/// its window with SetWindowPos rather than WPF's Window.Left/Top: Left/Top are DIPs scaled by
/// the *primary* monitor's DPI, so a position saved on a 150% laptop screen lands somewhere
/// else entirely when restored next to a 100% external monitor. Monitor work areas from
/// GetMonitorInfo are physical, so in physical pixels the two line up 1:1.
/// </summary>
internal static class MonitorHelper
{
    /// <summary>
    /// Nudges a window rectangle fully inside the work area of whichever monitor it mostly
    /// overlaps. If it overlaps none - the classic "second screen is unplugged" case - it is
    /// moved to the primary monitor instead.
    /// </summary>
    public static RECT ClampToVisibleWorkArea(RECT window)
    {
        RECT probe = window;

        // MONITOR_DEFAULTTONULL returns the monitor with the largest intersection, or NULL
        // when the rectangle is entirely off every screen.
        IntPtr monitor = MonitorFromRect(ref probe, MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero)
            monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero)
            return window;

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return window;

        RECT work = info.rcWork;
        int width = window.Width;
        int height = window.Height;

        // Math.Max guards the degenerate case of a window larger than the work area, where
        // the lower bound would otherwise exceed the upper bound and Clamp would throw.
        int x = Math.Clamp(window.Left, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - height));

        return new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    /// <summary>Default position for a first run: bottom-right of the primary work area.</summary>
    public static (int X, int Y) DefaultPosition(int width, int height)
    {
        IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return (100, 100);

        const int Margin = 24;
        return (info.rcWork.Right - width - Margin, info.rcWork.Bottom - height - Margin);
    }
}
