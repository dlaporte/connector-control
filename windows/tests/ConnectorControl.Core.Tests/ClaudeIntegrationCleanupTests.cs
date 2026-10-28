using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests;

/// <summary>Windows only: the Mac has no uninstall hook. Plain file work over a temp folder laid out as
/// LocalAppData and RoamingAppData, so it runs on any platform.</summary>
public class ClaudeIntegrationCleanupTests : IDisposable
{
    private sealed class FakeEnvironment(string? path) : IUserEnvironment
    {
        public string? Path { get; private set; } = path;
        public int Writes { get; private set; }
        public string? ReadPath() => Path;
        public void WritePath(string value)
        {
            Path = value;
            Writes++;
        }
    }

    private readonly TempDir dir = new("uninstall");
    private string Local => dir.File("Local");
    private string Roaming => dir.File("Roaming");
    private string DataDir => System.IO.Path.Combine(Local, AppPaths.DataDirName);
    private string Tool => ControlToolCopy.ToolPath(DataDir);
    private string Bin => ControlToolCopy.BinDir(DataDir);
    private string RoamingConfig => System.IO.Path.Combine(Roaming, "Claude", "claude_desktop_config.json");
    private string MsixConfig => System.IO.Path.Combine(Local, "Packages", ClaudePackage.StoreFamily, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
    private string SettingsConfig => dir.File("chosen/claude_desktop_config.json");

    public void Dispose() => dir.Dispose();

    private PathContext Paths(params (string Key, string Value)[] environment) =>
        new(environment.ToDictionary(pair => pair.Key, pair => pair.Value), new KnownFolders(Local, Roaming), new RealPathProbe());

    private static void Write(string path, params (string Name, JsonValue Entry)[] servers)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        ClaudeConfigIO.Write(servers.ToDictionary(server => server.Name, server => server.Entry, StringComparer.Ordinal), path);
    }

    private static JsonValue Github => JsonValue.Object(("command", JsonValue.String("npx")), ("args", JsonValue.Array([JsonValue.String("github")])));

    [Fact]
    public void EveryConfigTheAppMayHaveWrittenIsACandidate()
    {
        var env = dir.File("dev/claude_desktop_config.json");
        Write(MsixConfig);
        var candidates = ClaudeIntegrationCleanup.CandidateConfigs(Paths((AppPaths.ClaudeConfigEnv, env)), new PathOverrides(SettingsConfig, null));
        Assert.Equal([env, SettingsConfig, RoamingConfig, MsixConfig], candidates);
    }

    [Fact]
    public void OnlyTheAppsOwnEntryIsRemovedAndTheRestOfTheConfigStays()
    {
        Write(RoamingConfig, ("github", Github), (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool.ToUpperInvariant())));
        var backedUp = new List<string>();
        Assert.True(ClaudeIntegrationCleanup.RemoveEntry(RoamingConfig, Tool, backedUp.Add));
        Assert.Equal([RoamingConfig], backedUp);
        Assert.Equal(["github"], ClaudeConfigIO.ReadMcpServers(RoamingConfig).Keys);
        var mine = JsonValue.Object(("command", JsonValue.String(@"C:\mine\connector-control.exe")), ("args", JsonValue.Array([JsonValue.String("mcp")])));
        Write(SettingsConfig, (ClaudeIntegration.EntryName, mine));
        var before = File.ReadAllBytes(SettingsConfig);
        Assert.False(ClaudeIntegrationCleanup.RemoveEntry(SettingsConfig, Tool), "a connector of that name the person made stays");
        Assert.Equal(before, File.ReadAllBytes(SettingsConfig));
        Assert.False(ClaudeIntegrationCleanup.RemoveEntry(dir.File("missing.json"), Tool));
        File.WriteAllText(dir.File("broken.json"), "{ not json");
        Assert.False(ClaudeIntegrationCleanup.RemoveEntry(dir.File("broken.json"), Tool));
        Assert.Equal("{ not json", File.ReadAllText(dir.File("broken.json")));
    }

    [Fact]
    public void AConfigThatCannotBeBackedUpIsLeftAsItIs()
    {
        Write(RoamingConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        var before = File.ReadAllBytes(RoamingConfig);
        Assert.False(ClaudeIntegrationCleanup.RemoveEntry(RoamingConfig, Tool, _ => throw new IOException("disk full")));
        Assert.Equal(before, File.ReadAllBytes(RoamingConfig));
    }

    [Fact]
    public void UninstallingTakesTheEntryThePathFolderAndTheCopy()
    {
        Write(RoamingConfig, ("github", Github), (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        Write(MsixConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        Directory.CreateDirectory(Bin);
        File.WriteAllText(Tool, "tool");
        var environment = new FakeEnvironment(@"C:\Windows;" + Bin + @";C:\Tools");
        var paths = Paths();
        ClaudeIntegrationCleanup.Uninstall(paths, new PathOverrides(), DataDir, environment, BackupManager.DefaultKeepCount);
        Assert.Equal(["github"], ClaudeConfigIO.ReadMcpServers(RoamingConfig).Keys);
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(MsixConfig));
        Assert.Equal(@"C:\Windows;C:\Tools", environment.Path);
        Assert.False(Directory.Exists(Bin));
        var backups = AppPaths.Resolve(paths.Environment, new PathOverrides(), paths.Folders, paths.Probe).BackupsDir;
        Assert.Equal(2, Directory.EnumerateFiles(backups, RestoreModel.Series + ".*").Count());
    }

    [Fact]
    public void UninstallingWithNothingToTidyChangesNothing()
    {
        Write(RoamingConfig, ("github", Github));
        var before = File.ReadAllBytes(RoamingConfig);
        var environment = new FakeEnvironment(@"C:\Windows");
        ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, environment, BackupManager.DefaultKeepCount);
        Assert.Equal(before, File.ReadAllBytes(RoamingConfig));
        Assert.Equal(0, environment.Writes);
    }
}
