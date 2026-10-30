using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Windows only: the Mac counterpart is Tests/ConnectorControlStateTests/Transport/SymlinkCommandLineToolTests.swift.
/// The registry stands behind a fake, so this runs on any platform.</summary>
public class UserPathCommandLineToolTests
{
    private const string Bin = @"C:\Users\me\AppData\Local\Connector Control\bin";

    private sealed class FakeEnvironment(string? path) : IUserEnvironment
    {
        public string? Path { get; private set; } = path;
        public Exception? Failure { get; set; }
        public int Writes { get; private set; }
        public Exception? ReadFailure { get; set; }
        /// <summary>Runs as the PATH is written, before it is: what the tool shows while its change is announced.</summary>
        public Action? WhileWriting { get; set; }
        public string? ReadPath() => ReadFailure is null ? Path : throw ReadFailure;
        public void WritePath(string value)
        {
            WhileWriting?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }
            Path = value;
            Writes++;
        }
    }

    /// <summary>The tool over <see cref="Bin"/>, whose copy is taken to be there.</summary>
    private static UserPathCommandLineTool Tool(IUserEnvironment environment) =>
        new(Bin, environment, AppHost.Inline(), toolExists: _ => true);

    private static string? Answer(Action<Action<string?>> act)
    {
        string? said = "(no answer)";
        act(answer => said = answer);
        return said;
    }

    [Fact]
    public void InstallingAddsTheCopysFolderAndRemovingTakesItOut()
    {
        var environment = new FakeEnvironment(@"C:\Windows");
        // An inline host runs the background work and the answer on the test's own thread.
        var tool = Tool(environment);
        Assert.StartsWith(Bin, tool.ToolPath);
        Assert.EndsWith("connector-control.exe", tool.ToolPath);
        Assert.Null(tool.InstalledLocation);   // Windows puts a folder in the user PATH, with no one place to name
        Assert.False(tool.IsInstalled);
        var busy = new List<bool>();
        environment.WhileWriting = () => busy.Add(tool.IsBusy);
        Assert.Null(Answer(tool.Install));
        Assert.Equal([true], busy);   // busy while the PATH's change is written and announced
        Assert.False(tool.IsBusy);
        Assert.True(tool.IsInstalled);
        Assert.Equal(@"C:\Windows;" + Bin, environment.Path);
        Assert.Null(Answer(tool.Install));
        Assert.Equal(1, environment.Writes);
        Assert.Null(Answer(tool.Remove));
        Assert.False(tool.IsInstalled);
        Assert.Equal(@"C:\Windows", environment.Path);
    }

    [Fact]
    public void AFailureIsReportedInTheAppsWords()
    {
        var environment = new FakeEnvironment(@"C:\Windows") { Failure = new UnauthorizedAccessException("Access denied.") };
        var tool = Tool(environment);
        Assert.Equal(CommandLineToolText.InstallFailedError("Access denied."), Answer(tool.Install));
        Assert.False(tool.IsInstalled);
    }

    /// <summary>The last failure stays on the seam for any Settings window to show, until the next try clears it.
    /// Each start and end is announced.</summary>
    [Fact]
    public void TheLastFailureStaysUntilTheNextTryAndEachStartAndEndIsAnnounced()
    {
        var environment = new FakeEnvironment(@"C:\Windows") { Failure = new UnauthorizedAccessException("Access denied.") };
        var tool = Tool(environment);
        var changes = 0;
        tool.Changed += () => changes++;
        Assert.Equal(CommandLineToolText.InstallFailedError("Access denied."), Answer(tool.Install));
        Assert.Equal(CommandLineToolText.InstallFailedError("Access denied."), tool.LastProblem);
        Assert.Equal(2, changes);   // as the change starts and as it ends
        environment.Failure = null;
        var during = new List<string?>();
        environment.WhileWriting = () => during.Add(tool.LastProblem);
        Assert.Null(Answer(tool.Install));
        Assert.Equal([null], during);   // the next try clears it
        Assert.Null(tool.LastProblem);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void RemovingWhenTheFolderIsAbsentChangesNothing()
    {
        var environment = new FakeEnvironment(@"C:\Windows");
        var tool = Tool(environment);
        Assert.Null(Answer(tool.Remove));
        Assert.Equal(0, environment.Writes);
        Assert.Equal(@"C:\Windows", environment.Path);
    }

    [Fact]
    public void AnUnreadablePathIsNotInstalled()
    {
        var environment = new FakeEnvironment(@"C:\Windows") { ReadFailure = new IOException("locked") };
        Assert.False(Tool(environment).IsInstalled);
    }

    /// <summary>A user PATH that is not text - the registry's reader refuses it - is not installed, and neither installing
    /// nor removing writes over it: each says why.</summary>
    [Fact]
    public void APathThatIsNotTextIsLeftAlone()
    {
        var environment = new FakeEnvironment(@"C:\Windows") { ReadFailure = new IOException(UserPath.NotTextError) };
        var tool = Tool(environment);
        Assert.False(tool.IsInstalled);
        Assert.Equal(CommandLineToolText.InstallFailedError(UserPath.NotTextError), Answer(tool.Install));
        Assert.Equal(CommandLineToolText.RemoveFailedError(UserPath.NotTextError), Answer(tool.Remove));
        Assert.Equal(0, environment.Writes);
    }

    [Fact]
    public void AnythingThatGoesWrongStillAnswers()
    {
        var environment = new FakeEnvironment(@"C:\Windows") { Failure = new InvalidOperationException("Surprise.") };
        var tool = Tool(environment);
        Assert.Equal(CommandLineToolText.InstallFailedError("Surprise."), Answer(tool.Install));
        environment.ReadFailure = new ArgumentException("Odd.");
        Assert.Equal(CommandLineToolText.RemoveFailedError("Odd."), Answer(tool.Remove));
    }

    /// <summary>The copy in the folder is made as the app starts. When that failed, installing would put a folder with
    /// no tool in it on PATH, and Settings would say it is there: the install is refused, saying why, and the PATH is
    /// left alone. Without a check of its own the tool looks for the file itself.</summary>
    [Fact]
    public void InstallingWithoutTheToolsCopyIsRefused()
    {
        var environment = new FakeEnvironment(@"C:\Windows");
        var tool = new UserPathCommandLineTool(Bin, environment, AppHost.Inline(), toolExists: _ => false);
        Assert.Equal(CommandLineToolText.InstallFailedError(CommandLineToolText.MissingToolError(tool.ToolPath)), Answer(tool.Install));
        Assert.Equal(0, environment.Writes);
        Assert.False(tool.IsInstalled);

        using var dir = new TempDir("cli-copy");
        var onDisk = new UserPathCommandLineTool(dir.Path, environment, AppHost.Inline());
        Assert.Equal(CommandLineToolText.InstallFailedError(CommandLineToolText.MissingToolError(onDisk.ToolPath)), Answer(onDisk.Install));
        File.WriteAllText(onDisk.ToolPath, "");
        Assert.Null(Answer(onDisk.Install));
        Assert.True(onDisk.IsInstalled);
    }
}
