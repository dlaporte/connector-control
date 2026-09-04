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
    }

    [Fact]
    public void FolderScanWinsOverLegacyExe()
    {
        var probe = new FakePathProbe()
            .AddDirectory(Path.Combine(Local, "Packages", "Claude_pzs8sxrjxfjjc"))
            .AddFile(Path.Combine(Local, "AnthropicClaude", "claude.exe"));
        // The CI runner has no Claude package, so the WinRT step yields nothing and the scan decides.
        Assert.Equal(ClaudeInstallKind.Msix, new ClaudeInstall(Folders, probe).Detect().Kind);
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
