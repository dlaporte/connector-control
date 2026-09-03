using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class AppPathsTests
{
    private static readonly string Local = Path.Combine(Path.GetTempPath(), "Users", "me", "AppData", "Local");
    private static readonly string Roaming = Path.Combine(Path.GetTempPath(), "Users", "me", "AppData", "Roaming");
    private static readonly KnownFolders Folders = new(Local, Roaming);
    private static readonly Dictionary<string, string> NoEnv = new();
    private static readonly PathOverrides NoOverrides = new();

    private static string Pkg(string family) => Path.Combine(Local, "Packages", family);
    private static string PkgConfig(string family) => Path.Combine(Pkg(family), "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");

    [Fact]
    public void LiveDefaultsPointAtClaudeAndConnectorControl()
    {
        // testLiveDefaultsPointAtClaudeAndConnectorControl, Windows edition (no MSIX package present).
        var paths = AppPathsResolver.Resolve(NoEnv, NoOverrides, Folders, new FakePathProbe());
        Assert.Equal(Path.Combine(Roaming, "Claude", "claude_desktop_config.json"), paths.ClaudeConfigPath);
        Assert.Equal(Path.Combine(Local, "Connector Control"), paths.StoreDir);
        Assert.Equal("mcps.json", Path.GetFileName(paths.MasterStorePath));
        Assert.Equal(Path.Combine(Local, "Connector Control", "backups"), paths.BackupsDir);
    }

    [Fact]
    public void EnvironmentOverrides()
    {
        // testEnvironmentOverrides: backups follow the env store dir (dev sandbox).
        var x = Path.Combine(Path.GetTempPath(), "x");
        var env = new Dictionary<string, string>
        {
            [AppPathsResolver.ClaudeConfigEnv] = Path.Combine(x, "claude.json"),
            [AppPathsResolver.StoreDirEnv] = Path.Combine(x, "store"),
        };
        var paths = AppPathsResolver.Resolve(env, new PathOverrides(ClaudeConfigPath: "ignored", MasterStoreDir: "ignored"), Folders, new FakePathProbe());
        Assert.Equal(Path.Combine(x, "claude.json"), paths.ClaudeConfigPath);
        Assert.Equal(Path.Combine(x, "store"), paths.StoreDir);
        Assert.Equal(Path.Combine(x, "store", "mcps.json"), paths.MasterStorePath);
        Assert.Equal(Path.Combine(x, "store", "backups"), paths.BackupsDir);
    }

    [Fact]
    public void ExplicitBackupsDirIsHonoredIndependentlyOfStoreDir()
    {
        // testExplicitBackupsDirURLIsHonoredIndependentlyOfStoreDir
        var paths = new AppPaths("/tmp/x/claude.json", "/tmp/x/store", "/tmp/machine-local/backups");
        Assert.Equal("/tmp/machine-local/backups", paths.BackupsDir);
        Assert.Equal(Path.Combine("/tmp/x/store", "mcps.json"), paths.MasterStorePath);
    }

    [Fact]
    public void CustomMasterStoreDirKeepsBackupsAtTheDefault()
    {
        var custom = Path.Combine(Path.GetTempPath(), "Dropbox", "cc");
        var paths = AppPathsResolver.Resolve(NoEnv, new PathOverrides(MasterStoreDir: custom), Folders, new FakePathProbe());
        Assert.Equal(custom, paths.StoreDir);
        Assert.Equal(Path.Combine(Local, "Connector Control", "backups"), paths.BackupsDir);
    }

    [Fact]
    public void MsixPackageWithConfigWins()
    {
        var probe = new FakePathProbe().AddFile(PkgConfig("Claude_pzs8sxrjxfjjc"));
        var paths = AppPathsResolver.Resolve(NoEnv, NoOverrides, Folders, probe);
        Assert.Equal(PkgConfig("Claude_pzs8sxrjxfjjc"), paths.ClaudeConfigPath);
    }

    [Fact]
    public void MsixPackageWithoutConfigStillResolvesToItsLocalCache()
    {
        var probe = new FakePathProbe().AddDirectory(Pkg("Claude_pzs8sxrjxfjjc"));
        Assert.Equal(PkgConfig("Claude_pzs8sxrjxfjjc"), AppPathsResolver.ResolveMsixClaudeConfig(Folders, probe));
    }

    [Fact]
    public void PackageWithConfigIsPreferredOverOneWithout()
    {
        var probe = new FakePathProbe()
            .AddDirectory(Pkg("Anthropic.ClaudeDesktop_h6f0761"))
            .AddFile(PkgConfig("Claude_pzs8sxrjxfjjc"));
        Assert.Equal(PkgConfig("Claude_pzs8sxrjxfjjc"), AppPathsResolver.ResolveMsixClaudeConfig(Folders, probe));
    }

    [Fact]
    public void UnrelatedPackagesAreIgnored()
    {
        var probe = new FakePathProbe().AddDirectory(Pkg("Microsoft.WindowsTerminal_8wekyb3d8bbwe"));
        Assert.Null(AppPathsResolver.ResolveMsixClaudeConfig(Folders, probe));
    }

    [Fact]
    public void SettingsOverrideBeatsMsixAndEnvBeatsSettings()
    {
        var probe = new FakePathProbe().AddFile(PkgConfig("Claude_pzs8sxrjxfjjc"));
        var custom = Path.Combine(Path.GetTempPath(), "custom.json");
        var fromSettings = AppPathsResolver.Resolve(NoEnv, new PathOverrides(ClaudeConfigPath: custom), Folders, probe);
        Assert.Equal(custom, fromSettings.ClaudeConfigPath);
        var fromEnv = AppPathsResolver.Resolve(
            new Dictionary<string, string> { [AppPathsResolver.ClaudeConfigEnv] = "/env/claude.json" },
            new PathOverrides(ClaudeConfigPath: custom), Folders, probe);
        Assert.Equal("/env/claude.json", fromEnv.ClaudeConfigPath);
    }

    [Fact]
    public void EmptyOverridesCountAsAbsent()
    {
        var paths = AppPathsResolver.Resolve(
            new Dictionary<string, string> { [AppPathsResolver.StoreDirEnv] = "" },
            new PathOverrides(ClaudeConfigPath: "", MasterStoreDir: ""), Folders, new FakePathProbe());
        Assert.Equal(Path.Combine(Local, "Connector Control"), paths.StoreDir);
        Assert.Equal(Path.Combine(Roaming, "Claude", "claude_desktop_config.json"), paths.ClaudeConfigPath);
    }

    [Fact]
    public void RealProbeReadsTheFileSystem()
    {
        using var dir = new TempDir("probe");
        Directory.CreateDirectory(dir.File("a"));
        File.WriteAllText(dir.File("f.txt"), "x");
        var probe = new RealPathProbe();
        Assert.True(probe.DirectoryExists(dir.File("a")));
        Assert.True(probe.FileExists(dir.File("f.txt")));
        Assert.Equal([dir.File("a")], probe.EnumerateDirectories(dir.Path).ToArray());
        Assert.Empty(probe.EnumerateDirectories(dir.File("missing")));
    }
}
