using System.Runtime.InteropServices;
using System.Text;

namespace ConnectorControl.App.Services;

/// <summary>
/// Reads another process's executable path so Claude Desktop's processes can be
/// told apart from anything else called <c>claude</c> (spec §6.2).
/// <c>QueryFullProcessImageName</c> with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>
/// succeeds for the current user's processes, MSIX-packaged ones included, where
/// <c>Process.MainModule</c> needs far more access and fails across bitness.
/// </summary>
public static class ProcessImage
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ShortBuffer = 1024;
    private const int LongBuffer = 32768;   // the extended-path maximum

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    /// <summary>
    /// The full path of a process's executable, or null when it cannot be read:
    /// the process has exited, or it belongs to another user or a higher
    /// integrity level. Callers treat null as "not one of ours".
    /// </summary>
    public static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            return Query(handle, ShortBuffer) ?? Query(handle, LongBuffer);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// True when <paramref name="imagePath"/> sits inside <paramref name="directory"/>.
    /// Whole path segments only (so <c>C:\dir\sub</c> is not inside <c>C:\dir\subterranean</c>)
    /// and case-insensitive, as Windows paths are.
    /// </summary>
    public static bool IsUnder(string? imagePath, string? directory)
    {
        if (string.IsNullOrEmpty(imagePath) || string.IsNullOrEmpty(directory))
        {
            return false;
        }
        var root = directory.TrimEnd('\\', '/');
        return root.Length > 0
            && imagePath.Length > root.Length
            && IsSeparator(imagePath[root.Length])
            && imagePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static string? Query(nint handle, int capacity)
    {
        var buffer = new StringBuilder(capacity);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
    }
}
