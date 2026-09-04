using System.Drawing;
using System.Runtime.InteropServices;

namespace ConnectorControl.App.Views;

/// <summary>
/// Spec §7.1: the flyout opens beside the notification area (fallbacks: the
/// cursor, then the work-area corner). Every coordinate here is a physical
/// pixel — what TrayInfo.GetTrayLocation, GetCursorPos, GetMonitorInfo and
/// SetWindowPos all speak — so no DPI conversion happens anywhere.
/// </summary>
public static class WindowPlacement
{
    public const int Margin = 8;

    /// <summary>Pure placement rule, unit-tested: centered on the anchor's x, on the work-area side away from the taskbar, clamped inside the work area.</summary>
    public static Rectangle PlaceNear(Point anchor, Rectangle workArea, Size size)
    {
        int x = anchor.X - size.Width / 2;
        int y;
        if (anchor.Y >= workArea.Bottom)
        {
            y = workArea.Bottom - size.Height - Margin;   // taskbar at the bottom
        }
        else if (anchor.Y < workArea.Top)
        {
            y = workArea.Top + Margin;                    // taskbar at the top
        }
        else
        {
            y = anchor.Y - size.Height - Margin;          // side taskbars, or an anchor inside the work area
        }
        if (anchor.X < workArea.Left)
        {
            x = workArea.Left + Margin;                   // taskbar on the left
        }
        else if (anchor.X >= workArea.Right)
        {
            x = workArea.Right - size.Width - Margin;     // taskbar on the right
        }
        x = Math.Clamp(x, workArea.Left + Margin, Math.Max(workArea.Left + Margin, workArea.Right - size.Width - Margin));
        y = Math.Clamp(y, workArea.Top + Margin, Math.Max(workArea.Top + Margin, workArea.Bottom - size.Height - Margin));
        return new Rectangle(x, y, size.Width, size.Height);
    }

    /// <summary>
    /// Spec §7.1 anchoring rule, pure so it can be unit-tested: the notification
    /// area's own corner if the shell reported one, else the cursor, else the
    /// bottom-right corner of the work area. (0,0) from either source means
    /// "not reported" — neither the tray nor a real cursor ever sits there.
    /// </summary>
    public static Point Anchor(Point? tray, Point? cursor, Rectangle workArea)
    {
        if (tray is { IsEmpty: false } trayPoint)
        {
            return trayPoint;
        }
        if (cursor is { IsEmpty: false } cursorPoint)
        {
            return cursorPoint;
        }
        return new Point(workArea.Right, workArea.Bottom);
    }

    /// <summary>
    /// Moves an already-shown, already-laid-out window to the anchor. The same
    /// call serves the left-click toggle, the tray menu's Open item, and any
    /// programmatic show, so all three land in the same place (spec §7.1).
    /// </summary>
    /// <param name="trayAnchor">TrayInfo.GetTrayLocation(), or null when the shell did not answer.</param>
    public static void MoveNearTray(nint hwnd, Point? trayAnchor)
    {
        Point? cursor = GetCursorPos(out var raw) ? new Point(raw.X, raw.Y) : null;
        var workArea = WorkAreaAround(trayAnchor ?? cursor ?? Point.Empty);
        var anchor = Anchor(trayAnchor, cursor, workArea);
        GetWindowRect(hwnd, out var bounds);
        var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var target = PlaceNear(anchor, workArea, size);
        SetWindowPos(hwnd, HWND_TOPMOST, target.X, target.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Work area of the monitor nearest <paramref name="point"/>; a 1920×1080 guess if the query fails.</summary>
    private static Rectangle WorkAreaAround(Point point)
    {
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info)
            ? Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
            : new Rectangle(0, 0, 1920, 1080);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly nint HWND_TOPMOST = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
