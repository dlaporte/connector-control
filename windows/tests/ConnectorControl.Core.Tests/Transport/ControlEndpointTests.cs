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

    /// <summary>The app it would start edits the live Claude config, not the one named.</summary>
    [Fact]
    public void TheToolStartsNoAppWhileAnotherClaudeConfigIsNamed()
    {
        Assert.True(ControlEndpoint.MayStartApp(new Dictionary<string, string> { [AppPaths.ClaudeConfigEnv] = "" }));
        Assert.False(ControlEndpoint.MayStartApp(new Dictionary<string, string> { [AppPaths.ClaudeConfigEnv] = Path.Combine(Path.GetTempPath(), "claude_desktop_config.json") }));
    }

    /// <summary>The app the tool starts gets what Explorer would give it, not the caller's environment: no
    /// CONNECTOR_CONTROL_* override, and the PATH from the machine's and the user's settings, not a terminal's.</summary>
    [Fact]
    public void TheAppTheToolStartsGetsTheSignInEnvironmentNotTheCallers()
    {
        var caller = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\Users\me\.nvm\v20;C:\Windows",
            ["CONNECTOR_CONTROL_CLAUDE_CONFIG"] = @"C:\elsewhere\claude_desktop_config.json",
            ["connector_control_store_dir"] = @"C:\elsewhere\store",
            ["USERPROFILE"] = @"C:\Users\me",
            ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
        };
        var given = ControlEndpoint.AppEnvironment(caller, machinePath: @"C:\Windows\system32;C:\Windows", userPath: @"C:\Users\me\AppData\Local\Microsoft\WindowsApps");
        Assert.Equal(
            [("APPDATA", @"C:\Users\me\AppData\Roaming"), ("Path", @"C:\Windows\system32;C:\Windows;C:\Users\me\AppData\Local\Microsoft\WindowsApps"), ("USERPROFILE", @"C:\Users\me")],
            given);
        Assert.Equal("APPDATA=C:\\Users\\me\\AppData\\Roaming\0Path=C:\\Windows\\system32;C:\\Windows;C:\\Users\\me\\AppData\\Local\\Microsoft\\WindowsApps\0USERPROFILE=C:\\Users\\me\0\0",
            ControlEndpoint.EnvironmentBlock(given));
    }

    /// <summary>Settings that cannot be read leave the caller's PATH: an app with no PATH at all finds no tool.</summary>
    [Fact]
    public void ThePathStaysTheCallersOnlyWhenNoSettingCanBeRead()
    {
        var caller = new Dictionary<string, string> { ["PATH"] = @"C:\Windows" };
        Assert.Equal([("PATH", @"C:\Windows")], ControlEndpoint.AppEnvironment(caller, null, null));
        Assert.Equal([("Path", @"C:\Users\me\bin")], ControlEndpoint.AppEnvironment(caller, null, @"C:\Users\me\bin"));
        Assert.Equal([("Path", @"C:\Windows")], ControlEndpoint.AppEnvironment(caller, @"C:\Windows", ""));
    }

    /// <summary>A token whose owner is not its user is elevated: an administrator's elevated token is owned by
    /// BUILTIN\Administrators. One Windows cannot name counts as elevated, so the tool starts nothing.</summary>
    [Fact]
    public void ATokenOwnedByAnyoneButItsUserIsElevated()
    {
        Assert.False(ControlEndpoint.IsElevated(Sid, Sid));
        Assert.True(ControlEndpoint.IsElevated("S-1-5-32-544", Sid));
        Assert.True(ControlEndpoint.IsElevated(null, Sid));
        Assert.True(ControlEndpoint.IsElevated(Sid, null));
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

    [Fact]
    public void TheAppBesideTheToolWinsOverTheRecordedOne()
    {
        var current = Path.Combine(Path.GetTempPath(), "cc-endpoint", "current");
        var bin = Path.Combine(Path.GetTempPath(), "cc-endpoint", "bin");
        var other = Path.Combine(Path.GetTempPath(), "cc-endpoint", "other", ControlEndpoint.AppFileName);
        var beside = Path.Combine(bin, ControlEndpoint.AppFileName);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { beside, other };
        var texts = new Dictionary<string, string> { [Path.Combine(bin, ControlEndpoint.AppPathFileName)] = other };
        Assert.Equal(beside, ControlEndpoint.AppExecutable(Path.Combine(bin, ControlEndpoint.ToolFileName), files.Contains, texts.GetValueOrDefault));
    }

    [Fact]
    public void ANeverWrittenRecordNamesNoApp()
    {
        var bin = Path.Combine(Path.GetTempPath(), "cc-endpoint", "bin");
        Assert.Null(ControlEndpoint.AppExecutable(Path.Combine(bin, ControlEndpoint.ToolFileName), _ => false, _ => null));
    }

    [Fact]
    public void ARecordThatIsNotAFullPathNamesNoApp()
    {
        var bin = Path.Combine(Path.GetTempPath(), "cc-endpoint", "bin");
        var relative = Path.Combine("current", ControlEndpoint.AppFileName);
        var texts = new Dictionary<string, string> { [Path.Combine(bin, ControlEndpoint.AppPathFileName)] = relative };
        // a relative record would resolve against whatever folder the tool was run from
        Assert.Null(ControlEndpoint.AppExecutable(Path.Combine(bin, ControlEndpoint.ToolFileName), path => path == relative, texts.GetValueOrDefault));
    }

    [Theory]
    [InlineData("1.5.0+1a2b3c4", "1.5.0")]
    [InlineData("1.5.0-preview.1+1a2b3c4", "1.5.0-preview.1")]
    [InlineData("1.5.0", "1.5.0")]
    [InlineData("0.0.0+1a2b3c4", "development build")]
    [InlineData("0.0.0", "development build")]
    [InlineData("", "development build")]
    [InlineData("1.0", "development build")]
    public void TheToolSaysItsVersionOnlyWhenItIsOne(string stamped, string expected) =>
        Assert.Equal(expected, ControlEndpoint.ToolVersion(stamped));
}
