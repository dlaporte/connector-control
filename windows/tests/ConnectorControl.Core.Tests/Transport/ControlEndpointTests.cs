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

    /// <summary>The pipe's rule for who it serves, apart from any pipe: a client on this computer only, and
    /// anything Windows answers that says neither is taken as remote.</summary>
    [Fact]
    public void OnlyAClientOnThisComputerIsServed()
    {
        Assert.True(ControlPipe.IsLocalClient(false, ControlPipe.ErrorPipeLocal, null, "BUILD01"));
        Assert.True(ControlPipe.IsLocalClient(true, 0, "build01", "BUILD01"), "this computer's own name, in any case");
        Assert.False(ControlPipe.IsLocalClient(true, 0, "LAPTOP7", "BUILD01"), "another computer");
        Assert.False(ControlPipe.IsLocalClient(false, 6, null, "BUILD01"), "a failure that says neither");
    }
}
