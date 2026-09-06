using System.Runtime.InteropServices;
using System.Text;

namespace ConnectorControl.App.Services;

/// <summary>
/// Asks a GUI process to quit the way Windows does at logoff:
/// WM_QUERYENDSESSION, then WM_ENDSESSION with ENDSESSION_CLOSEAPP.
/// Probe-verified 2026-09-03 against Claude Desktop 1.37937: WM_CLOSE only
/// hides it to the tray and the Restart Manager fails, but this pair quits
/// it cleanly. Nothing here ever terminates a process.
/// </summary>
public static class SessionEnd
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const nint ENDSESSION_CLOSEAPP = 0x1;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint ReplyTimeoutMs = 5000;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    /// <summary>
    /// Top-level windows owned by the given processes that are visible or carry
    /// the main window title (a tray-hidden Electron window keeps its title).
    /// </summary>
    public static IReadOnlyList<nint> FindCandidateWindows(IReadOnlySet<int> processIds, string mainWindowTitle)
    {
        var result = new List<nint>();
        if (processIds.Count == 0)
        {
            return result;
        }
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (processIds.Contains((int)pid)
                && (IsWindowVisible(hWnd) || string.Equals(WindowTitle(hWnd), mainWindowTitle, StringComparison.Ordinal)))
            {
                result.Add(hWnd);
            }
            return true;
        }, 0);
        return result;
    }

    /// <summary>
    /// Sends WM_QUERYENDSESSION to each window and, when the window agrees,
    /// WM_ENDSESSION. Returns true when at least one window was told to end.
    /// </summary>
    public static bool RequestQuit(IEnumerable<nint> windows)
    {
        bool any = false;
        foreach (var hWnd in windows)
        {
            SendMessageTimeout(hWnd, WM_QUERYENDSESSION, 0, ENDSESSION_CLOSEAPP, SMTO_ABORTIFHUNG, ReplyTimeoutMs, out var agreed);
            if (agreed == 0)
            {
                continue;   // the app declined (or did not answer): leave it alone
            }
            SendMessageTimeout(hWnd, WM_ENDSESSION, 1, ENDSESSION_CLOSEAPP, SMTO_ABORTIFHUNG, ReplyTimeoutMs, out _);
            any = true;
        }
        return any;
    }

    private static string WindowTitle(nint hWnd)
    {
        var buffer = new StringBuilder(256);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
