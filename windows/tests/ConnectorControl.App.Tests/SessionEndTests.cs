using ConnectorControl.App.Services;

namespace ConnectorControl.App.Tests;

public class SessionEndTests
{
    [Fact]
    public void NoProcessesMeansNoWindows()
    {
        Assert.Empty(SessionEnd.FindCandidateWindows(new HashSet<int>(), "Claude"));
    }

    [Fact]
    public void EnumeratingOwnProcessDoesNotThrow()
    {
        var windows = SessionEnd.FindCandidateWindows(new HashSet<int> { Environment.ProcessId }, "no-such-title");
        Assert.NotNull(windows);   // a console test host usually has no visible windows; the point is a clean enumeration
    }

    [Fact]
    public void RequestQuitWithNoWindowsIsFalse()
    {
        Assert.False(SessionEnd.RequestQuit(Array.Empty<nint>()));
    }
}
