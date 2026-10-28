using ConnectorControl.Core.State;
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
        public string? ReadPath() => Path;
        public void WritePath(string value)
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            Path = value;
            Writes++;
        }
    }

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
        var tool = new UserPathCommandLineTool(Bin, environment, AppHost.Inline());
        // Path.Combine, so the test also holds where the separator is not a backslash.
        Assert.Equal(Path.Combine(Bin, ControlEndpoint.ToolFileName), tool.ToolPath);
        Assert.False(tool.IsInstalled);
        Assert.Null(Answer(tool.Install));
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
        var tool = new UserPathCommandLineTool(Bin, environment, AppHost.Inline());
        Assert.Equal(CommandLineToolText.InstallFailedError("Access denied."), Answer(tool.Install));
        Assert.False(tool.IsInstalled);
    }
}
