using System.Security;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Sources/ConnectorControlTransport/SymlinkCommandLineTool.swift.
///
/// The command-line tool on PATH as the folder of its copy in the user PATH. No prompt: the user PATH is the
/// user's own. A new terminal sees the change; one already open keeps the PATH it started with. The change runs
/// off the UI thread: its broadcast gives every window up to a second to answer. There is no check that the app runs
/// from where it will stay, as on the Mac: Windows installs into a fixed folder and is never transient.
/// </summary>
public sealed class UserPathCommandLineTool(string binDir, IUserEnvironment environment, AppHost host, Func<string, bool>? toolExists = null)
    : ICommandLineTool
{
    /// <summary>Whether the tool's copy is there; tests stand a fake in for the file system.</summary>
    private readonly Func<string, bool> toolExists = toolExists ?? File.Exists;

    private bool busy;
    private string? lastProblem;

    public event Action? Changed;

    public string ToolPath => Path.Combine(binDir, ControlEndpoint.ToolFileName);

    /// <summary>None: the user PATH holds a folder, with no one place to name.</summary>
    public string? InstalledLocation => null;

    /// <summary>While the change runs and is announced, off the UI thread.</summary>
    public bool IsBusy => busy;

    public string? LastProblem => lastProblem;

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

    /// <summary>Refused while the tool's copy is missing — its refresh as the app starts can fail — or the folder would be
    /// on PATH with nothing in it, and Settings would say the tool is there.</summary>
    // Platform idiom: the Mac's link points into the app's own bundle, which cannot be missing while it runs, so
    // the Mac checks nothing.
    public void Install(Action<string?> completion) => Change(path => toolExists(ToolPath)
        ? UserPath.Adding(path, binDir)
        : throw new IOException(CommandLineToolText.MissingToolError(ToolPath)), CommandLineToolText.InstallFailedError, completion);

    public void Remove(Action<string?> completion) => Change(path => UserPath.Removing(path, binDir), CommandLineToolText.RemoveFailedError, completion);

    private void Change(Func<string?, string> change, Func<string, string> failure, Action<string?> completion)
    {
        busy = true;
        lastProblem = null;
        Changed?.Invoke();
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
            catch (Exception ex)
            {
                // The task's boundary: whatever goes wrong becomes the sentence, so a bound UI is never left waiting.
                problem = failure(ex.Message);
            }
            host.Marshal(() =>
            {
                // Kept and announced before the completion runs, so every window open sees the end.
                busy = false;
                lastProblem = problem;
                Changed?.Invoke();
                completion(problem);
            });
        });
    }
}
