using System.Diagnostics;
using ConnectorControl.App.Services;
using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Tests;

public class ClaudeProcessTests
{
    private const string NoSuchProcess = "connector-control-no-such-process";

    private static ClaudeInstallInfo Legacy(string exe) => new(ClaudeInstallKind.Legacy, null, exe, NoSuchProcess);

    [Fact]
    public void NotRunningWhenNoProcessMatches()
    {
        var p = new ClaudeProcess(() => ClaudeInstallInfo.NotFound with { ProcessName = NoSuchProcess }, () => null);
        Assert.False(p.IsRunning);
        Assert.Null(p.LaunchTime);
    }

    [Fact]
    public async Task NotFoundInstallReportsMessage()
    {
        var p = new ClaudeProcess(() => ClaudeInstallInfo.NotFound with { ProcessName = NoSuchProcess }, () => null);
        Assert.Equal("Claude Desktop was not found on this PC.", await p.RestartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingLegacyExeReportsThePath()
    {
        var exe = Path.Combine(Path.GetTempPath(), "cc-missing", "claude.exe");
        var p = new ClaudeProcess(() => Legacy(exe), () => null);
        Assert.Equal($"Claude was not found at {exe}.", await p.RestartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LaunchTargetOverrideBeatsDetection()
    {
        var detected = Path.Combine(Path.GetTempPath(), "cc-missing", "detected.exe");
        var overridden = Path.Combine(Path.GetTempPath(), "cc-missing", "override.exe");
        var p = new ClaudeProcess(() => Legacy(detected), () => overridden);
        Assert.Equal($"Claude was not found at {overridden}.", await p.RestartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyOverrideIsIgnored()
    {
        var detected = Path.Combine(Path.GetTempPath(), "cc-missing", "detected.exe");
        var p = new ClaudeProcess(() => Legacy(detected), () => "");
        Assert.Equal($"Claude was not found at {detected}.", await p.RestartAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Claude_pzs8sxrjxfjjc!Claude", true)]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\claude.exe", false)]
    [InlineData(@"C:\odd!name\claude.exe", false)]
    public void AumidRecognition(string target, bool expected)
    {
        Assert.Equal(expected, ClaudeProcess.IsAumid(target));
    }

    [Fact]
    public void RunningIsTrueForAProcessInsideTheInstallDirectory()
    {
        // This test process stands in for Claude: same name, same session, and it
        // does live under the directory we claim as the install.
        using var self = Process.GetCurrentProcess();
        var exe = Environment.ProcessPath!;
        var info = new ClaudeInstallInfo(ClaudeInstallKind.Legacy, null, exe, self.ProcessName, Path.GetDirectoryName(exe));
        var p = new ClaudeProcess(() => info, () => null);
        Assert.True(p.IsRunning);
        Assert.NotNull(p.LaunchTime);
    }

    [Fact]
    public void ProcessesOutsideTheInstallDirectoryAreNotClaude()
    {
        // The Claude Code CLI's binary is also called claude.exe: same name, wrong place.
        using var self = Process.GetCurrentProcess();
        var elsewhere = Path.Combine(Path.GetTempPath(), "cc-not-the-install");
        var info = new ClaudeInstallInfo(ClaudeInstallKind.Msix, "Claude_x", "Claude_x!Claude", self.ProcessName, elsewhere);
        var p = new ClaudeProcess(() => info, () => null);
        Assert.False(p.IsRunning);
        Assert.Null(p.LaunchTime);
    }

    [Fact]
    public void UnknownInstallDirectoryStillMatchesByName()
    {
        using var self = Process.GetCurrentProcess();
        var info = new ClaudeInstallInfo(ClaudeInstallKind.Msix, "Claude_x", "Claude_x!Claude", self.ProcessName);
        var p = new ClaudeProcess(() => info, () => null);
        Assert.Null(info.InstallDirectory);
        Assert.True(p.IsRunning);
    }

    [Fact]
    public void QuitMessageMatchesTheMacApp()
    {
        Assert.Equal("Claude didn\u2019t quit (it may be showing a dialog). Quit it manually, then click Restart Claude again.", ClaudeProcess.DidNotQuitMessage);
    }
}
