using System.Security;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Sources/ConnectorControlTransport/SymlinkCommandLineTool.swift.
///
/// The command-line tool on PATH as the folder of its copy in the user PATH. No prompt: the user PATH is the
/// user's own. A new terminal sees the change; one already open keeps the PATH it started with. The change runs
/// off the UI thread: its broadcast gives every window up to five seconds to answer.
/// </summary>
public sealed class UserPathCommandLineTool(string binDir, IUserEnvironment environment, AppHost host) : ICommandLineTool
{
    public string ToolPath => Path.Combine(binDir, ControlEndpoint.ToolFileName);

    public bool IsInstalled
    {
        get
        {
            try
            {
                return UserPath.Contains(environment.ReadPath(), binDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }
    }

    public void Install(Action<string?> completion) => Change(path => UserPath.Adding(path, binDir), CommandLineToolText.InstallFailedError, completion);

    public void Remove(Action<string?> completion) => Change(path => UserPath.Removing(path, binDir), CommandLineToolText.RemoveFailedError, completion);

    private void Change(Func<string?, string> change, Func<string, string> failure, Action<string?> completion) =>
        host.Background(() =>
        {
            string? problem = null;
            try
            {
                var path = environment.ReadPath();
                var changed = change(path);
                if (changed != (path ?? ""))
                {
                    environment.WritePath(changed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                problem = failure(ex.Message);
            }
            host.Marshal(() => completion(problem));
        });
}
