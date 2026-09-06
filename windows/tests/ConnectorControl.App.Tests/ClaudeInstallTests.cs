using ConnectorControl.App.Services;
using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class ClaudeInstallTests
{
    private static readonly string Local = Path.Combine(Path.GetTempPath(), "cc-install", "Local");
    private static readonly string Roaming = Path.Combine(Path.GetTempPath(), "cc-install", "Roaming");
    private static readonly KnownFolders Folders = new(Local, Roaming);

    [Theory]
    [InlineData("Claude_pzs8sxrjxfjjc", true)]
    [InlineData("Anthropic.ClaudeDesktop_h6f0761", true)]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", false)]
    [InlineData("claude_lowercase", false)]
    public void IsClaudeFamily(string family, bool expected)
    {
        Assert.Equal(expected, ClaudeInstall.IsClaudeFamily(family));
    }

    [Fact]
    public void FolderScanDetectsMsixPackageAndDerivesAumid()
    {
        var probe = new FakePathProbe().AddDirectory(Path.Combine(Local, "Packages", "Claude_pzs8sxrjxfjjc"));
        var info = new ClaudeInstall(Folders, probe).DetectMsixByFolderScan();
        Assert.NotNull(info);
        Assert.Equal(ClaudeInstallKind.Msix, info.Kind);
        Assert.Equal("Claude_pzs8sxrjxfjjc", info.PackageFamilyName);
        Assert.Equal("Claude_pzs8sxrjxfjjc!Claude", info.LaunchTarget);
        Assert.Equal("claude", info.ProcessName);
    }

    [Fact]
    public void FolderScanIgnoresUnrelatedPackages()
    {
        var probe = new FakePathProbe().AddDirectory(Path.Combine(Local, "Packages", "Microsoft.Paint_8wekyb3d8bbwe"));
        Assert.Null(new ClaudeInstall(Folders, probe).DetectMsixByFolderScan());
    }

    [Fact]
    public void LegacyExeIsDetectedWhenNoPackageExists()
    {
        var exe = Path.Combine(Local, "AnthropicClaude", "claude.exe");
        var probe = new FakePathProbe().AddFile(exe);
        var info = new ClaudeInstall(Folders, probe).Detect();
        Assert.Equal(ClaudeInstallKind.Legacy, info.Kind);
        Assert.Equal(exe, info.LaunchTarget);
        Assert.Null(info.PackageFamilyName);
        Assert.Equal("claude", info.ProcessName);
        Assert.Equal(Path.GetDirectoryName(exe), info.InstallDirectory);
    }

    [Fact]
    public void LegacyExeWinsWhenWinRtReportsNoPackage()
    {
        var probe = new FakePathProbe()
            .AddDirectory(Path.Combine(Local, "Packages", "Claude_pzs8sxrjxfjjc"))
            .AddFile(Path.Combine(Local, "AnthropicClaude", "claude.exe"));
        // The CI runner has no Claude package, so WinRT succeeds with no match. A leftover
        // package folder must not shadow the legacy install (spec §6.1).
        Assert.Equal(ClaudeInstallKind.Legacy, new ClaudeInstall(Folders, probe).Detect().Kind);
    }

    [Fact]
    public void FolderScanIsUsedOnlyWhenTheWinRtQueryFails()
    {
        var probe = new FakePathProbe()
            .AddDirectory(Path.Combine(Local, "Packages", "Claude_pzs8sxrjxfjjc"))
            .AddFile(Path.Combine(Local, "AnthropicClaude", "claude.exe"));
        var install = new ClaudeInstall(Folders, probe);
        var scanned = install.Detect(() => ClaudeInstall.MsixLookup.Unavailable);
        Assert.Equal(ClaudeInstallKind.Msix, scanned.Kind);
        Assert.Equal("Claude_pzs8sxrjxfjjc!Claude", scanned.LaunchTarget);
        Assert.Null(scanned.InstallDirectory);   // not derivable from the family name
        Assert.Equal(ClaudeInstallKind.Legacy, install.Detect(() => ClaudeInstall.MsixLookup.NotInstalled).Kind);
    }

    [Fact]
    public void WinRtResultWinsOverEverythingElse()
    {
        var probe = new FakePathProbe()
            .AddDirectory(Path.Combine(Local, "Packages", "Claude_scanned"))
            .AddFile(Path.Combine(Local, "AnthropicClaude", "claude.exe"));
        var found = new ClaudeInstallInfo(ClaudeInstallKind.Msix, "Claude_winrt", "Claude_winrt!Claude", "claude", @"C:\Program Files\WindowsApps\Claude_winrt");
        var info = new ClaudeInstall(Folders, probe).Detect(() => ClaudeInstall.MsixLookup.Found(found));
        Assert.Same(found, info);
    }

    [Fact]
    public void NothingFoundWhenWinRtFailsAndNothingIsInstalled()
    {
        var install = new ClaudeInstall(Folders, new FakePathProbe());
        Assert.Equal(ClaudeInstallInfo.NotFound, install.Detect(() => ClaudeInstall.MsixLookup.Unavailable));
    }

    [Fact]
    public void NothingInstalledIsNotFound()
    {
        var info = new ClaudeInstall(Folders, new FakePathProbe()).Detect();
        Assert.Equal(ClaudeInstallInfo.NotFound, info);
    }

    [Fact]
    public void RealDetectionDoesNotThrow()
    {
        // Exercises the WinRT PackageManager path on the runner (no Claude installed there).
        var info = new ClaudeInstall(KnownFolders.Current(), new RealPathProbe()).Detect();
        Assert.NotNull(info);
        Assert.Equal("claude", info.ProcessName);
    }
}
