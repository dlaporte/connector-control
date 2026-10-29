using System.Runtime.InteropServices;
using System.Text;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Cli;

/// <summary>
/// Windows only: the Mac counterpart is <c>ControlEndpoint.launchApp</c> in
/// Sources/ConnectorControlTransport/ControlEndpoint.swift, which asks <c>open</c> to do this.
///
/// Starts the app as a process of its own. Claude Desktop runs the relay in a job object, and a child
/// that inherited the relay's handles — Claude's pipes among them — or stayed in that job could hold
/// Claude's pipes open after the relay ends, or be ended when Claude quits. So nothing is inherited,
/// and the app leaves the job where the job allows it; where it does not, it starts inside it. Nor does it
/// inherit the tool's environment, which is Claude's or a terminal's: it gets the block
/// <see cref="ControlEndpoint.AppEnvironment"/> builds, as the Mac's <c>open</c> gives it launchd's own.
/// </summary>
internal static class DetachedProcess
{
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>Starts <paramref name="executable"/> with <paramref name="argument"/> and <paramref name="environment"/>
    /// (<see cref="ControlEndpoint.EnvironmentBlock"/>'s form); false when Windows would not.</summary>
    public static bool Start(string executable, string argument, string environment)
    {
        var block = Marshal.StringToHGlobalUni(environment);
        try
        {
            foreach (var flags in new[] { CreateBreakawayFromJob | CreateNewProcessGroup, CreateNewProcessGroup })
            {
                var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>() };
                var commandLine = new StringBuilder($"\"{executable}\" {argument}");
                if (CreateProcess(executable, commandLine, 0, 0, false, flags | CreateUnicodeEnvironment, block, Path.GetDirectoryName(executable),
                        ref startup, out var started))
                {
                    CloseHandle(started.Thread);
                    CloseHandle(started.Process);
                    return true;
                }
                if (Marshal.GetLastPInvokeError() != ErrorAccessDenied)
                {
                    return false;   // only a job that will not let the app leave is worth a second try
                }
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>
    /// What the app is started with: <see cref="ControlEndpoint.AppEnvironment"/> over this process's environment
    /// and the machine's and the user's PATH settings.
    /// </summary>
    public static string AppEnvironmentBlock(IReadOnlyDictionary<string, string> current) =>
        ControlEndpoint.EnvironmentBlock(ControlEndpoint.AppEnvironment(current, SettingPath(EnvironmentVariableTarget.Machine), SettingPath(EnvironmentVariableTarget.User)));

    /// <summary>The PATH saved for <paramref name="target"/>; null when it cannot be read. .NET reads the registry value
    /// already expanded, as Explorer does; expanding it again would use this tool's environment, a terminal's.</summary>
    private static string? SettingPath(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable("Path", target);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
