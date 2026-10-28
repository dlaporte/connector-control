using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Windows only, apart from <c>MayStartApp</c>: the Mac counterpart is
/// Tests/ConnectorControlStateTests/Transport/ControlEndpointTests.swift. Pure, so it runs on any platform.</summary>
public class ControlEndpointTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private static Dictionary<string, string> Store(string folder) => new() { [AppPaths.StoreDirEnv] = folder };

    [Fact]
    public void ThePipeIsNamedForTheUserAndMovesWithADevelopmentRunsStore()
    {
        Assert.Equal("ConnectorControl-" + Sid, ControlEndpoint.PipeName(new Dictionary<string, string>(), Sid));
        Assert.Equal("ConnectorControl-" + Sid, ControlEndpoint.PipeName(Store(""), Sid));
        var folder = Path.Combine(Path.GetTempPath(), "cc-endpoint", "store");
        var moved = ControlEndpoint.PipeName(Store(folder), Sid);
        Assert.Matches("^ConnectorControl-" + Sid + "-[0-9a-f]{16}$", moved);
        Assert.Equal(moved, ControlEndpoint.PipeName(Store(folder + Path.DirectorySeparatorChar), Sid));
        Assert.Equal(moved, ControlEndpoint.PipeName(Store(folder.ToUpperInvariant()), Sid));
        Assert.NotEqual(moved, ControlEndpoint.PipeName(Store(folder + "2"), Sid));
    }

    [Fact]
    public void TheToolStartsTheAppOnlyAgainstTheLiveStore()
    {
        Assert.True(ControlEndpoint.MayStartApp(new Dictionary<string, string>()));
        Assert.True(ControlEndpoint.MayStartApp(Store("")));
        Assert.False(ControlEndpoint.MayStartApp(Store(Path.GetTempPath())));
    }

    /// <summary>The pipe's rule for who it serves, apart from any pipe: a client on this computer only, which
    /// Windows answers with one failure; a name, or any other answer, is taken as remote.</summary>
    [Fact]
    public void OnlyAClientOnThisComputerIsServed()
    {
        Assert.True(ControlPipe.IsLocalClient(false, ControlPipe.ErrorPipeLocal));
        Assert.False(ControlPipe.IsLocalClient(true, 0), "a computer name, whoever's, is not proof of being local");
        Assert.False(ControlPipe.IsLocalClient(false, 6), "a failure that says neither");
        Assert.False(ControlPipe.IsLocalClient(false, 0));
    }

    /// <summary>The packaged tool starts the app beside it; the copy in bin\ starts the app that last refreshed
    /// it, as that app recorded; anything else names no app.</summary>
    [Fact]
    public void TheToolFindsTheAppBesideItOrWhereTheAppRecordedIt()
    {
        var current = Path.Combine(Path.GetTempPath(), "cc-endpoint", "current");
        var bin = Path.Combine(Path.GetTempPath(), "cc-endpoint", "bin");
        var app = Path.Combine(current, ControlEndpoint.AppFileName);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { app };
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Path.Combine(bin, ControlEndpoint.AppPathFileName)] = app + "\r\n" };
        string? Read(string path) => texts.GetValueOrDefault(path);
        Assert.Equal(app, ControlEndpoint.AppExecutable(Path.Combine(current, ControlEndpoint.ToolFileName), files.Contains, Read));
        Assert.Equal(app, ControlEndpoint.AppExecutable(Path.Combine(bin, ControlEndpoint.ToolFileName), files.Contains, Read));
        files.Clear();
        Assert.Null(ControlEndpoint.AppExecutable(Path.Combine(bin, ControlEndpoint.ToolFileName), files.Contains, Read));
        texts[Path.Combine(bin, ControlEndpoint.AppPathFileName)] = Path.Combine(current, "notepad.exe");
        files.Add(Path.Combine(current, "notepad.exe"));
        Assert.Null(ControlEndpoint.AppExecutable(Path.Combine(bin, ControlEndpoint.ToolFileName), files.Contains, Read));
    }
}
