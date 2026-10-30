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
        var reported = new List<string>();
        ClaudeIntegrationCleanup.Uninstall(paths, new PathOverrides(), DataDir, environment, BackupManager.DefaultKeepCount, reported.Add);
        Assert.Equal(["github"], ClaudeConfigIO.ReadMcpServers(RoamingConfig).Keys);
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(MsixConfig));
        Assert.Equal(@"C:\Windows;C:\Tools", environment.Path);
        Assert.False(Directory.Exists(Bin));
        var backups = AppPaths.Resolve(paths.Environment, new PathOverrides(), paths.Folders, paths.Probe).BackupsDir;
        Assert.Equal(2, Directory.EnumerateFiles(backups, RestoreModel.Series + ".*").Count());
        Assert.Empty(reported);
    }

    [Fact]
    public void UninstallingWithNothingToTidyChangesNothing()
    {
        Write(RoamingConfig, ("github", Github));
        var before = File.ReadAllBytes(RoamingConfig);
        var environment = new FakeEnvironment(@"C:\Windows");
        var reported = new List<string>();
        var paths = Paths();
        ClaudeIntegrationCleanup.Uninstall(paths, new PathOverrides(), DataDir, environment, BackupManager.DefaultKeepCount, reported.Add);
        Assert.Equal(before, File.ReadAllBytes(RoamingConfig));
        Assert.Equal(0, environment.Writes);
        Assert.Empty(reported);
        var backups = AppPaths.Resolve(paths.Environment, new PathOverrides(), paths.Folders, paths.Probe).BackupsDir;
        Assert.False(Directory.Exists(backups) && Directory.EnumerateFileSystemEntries(backups).Any(), "no backup for a config that was not changed");
    }

    [Fact]
    public void AKeepCountOfOneLeavesOneBackupOverTwoConfigs()
    {
        // The two differ, or the second backup would be skipped as a copy of the first whatever the count.
        Write(RoamingConfig, ("github", Github), (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        Write(MsixConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        var paths = Paths();
        ClaudeIntegrationCleanup.Uninstall(paths, new PathOverrides(), DataDir, new FakeEnvironment(null), 1);
        var backups = AppPaths.Resolve(paths.Environment, new PathOverrides(), paths.Folders, paths.Probe).BackupsDir;
        Assert.Single(Directory.EnumerateFiles(backups, RestoreModel.Series + ".*"));
    }

    [Fact]
    public void AnEntryThatIsNotExactlyTheAppsIsLeftAlone()
    {
        JsonValue Entry(JsonValue command, JsonValue args) => JsonValue.Object(("command", command), ("args", args));
        var command = JsonValue.String(Tool);
        JsonValue[] others =
        [
            Entry(command, JsonValue.Array([JsonValue.String("mcp"), JsonValue.String("x")])),
            Entry(command, JsonValue.Array([JsonValue.String("MCP")])),
            Entry(JsonValue.Array([command]), JsonValue.Array([JsonValue.String("mcp")])),
            Entry(JsonValue.Int(1), JsonValue.Array([JsonValue.String("mcp")])),
        ];
        foreach (var other in others)
        {
            Write(RoamingConfig, (ClaudeIntegration.EntryName, other));
            var before = File.ReadAllBytes(RoamingConfig);
            Assert.False(ClaudeIntegrationCleanup.RemoveEntry(RoamingConfig, Tool));
            Assert.Equal(before, File.ReadAllBytes(RoamingConfig));
        }
    }

    [Fact]
    public void AnEntryWithAnEnvKeyIsStillTheAppsAndGoes()
    {
        // It runs a copy that is being deleted, whatever else it carries.
        var entry = JsonValue.Object(("command", JsonValue.String(Tool)), ("args", JsonValue.Array([JsonValue.String("mcp")])),
            ("env", JsonValue.Object(("X", JsonValue.String("1")))));
        Write(RoamingConfig, (ClaudeIntegration.EntryName, entry));
        Assert.True(ClaudeIntegrationCleanup.RemoveEntry(RoamingConfig, Tool));
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(RoamingConfig));
    }

    [Fact]
    public void AnEntryThatCannotBeBackedUpIsReported()
    {
        Write(RoamingConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        var reported = new List<string>();
        Assert.False(ClaudeIntegrationCleanup.RemoveEntry(RoamingConfig, Tool, _ => throw new IOException("disk full"), reported.Add));
        Assert.Contains("disk full", Assert.Single(reported));
        File.WriteAllText(dir.File("broken.json"), "{ not json");
        Assert.False(ClaudeIntegrationCleanup.RemoveEntry(dir.File("broken.json"), Tool, null, reported.Add));
        Assert.Equal(2, reported.Count);
        reported.Clear();
        Assert.False(ClaudeIntegrationCleanup.RemoveEntry(dir.File("missing.json"), Tool, null, reported.Add));
        Assert.Empty(reported);
    }

    [Fact]
    public void AConfigWhoseWriteFailsIsReportedAndTheCopyAndPathStillGo()
    {
        Write(RoamingConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        Write(MsixConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        Directory.CreateDirectory(Bin);
        File.WriteAllText(Tool, "tool");
        var environment = new FakeEnvironment(@"C:\Windows;" + Bin);
        var reported = new List<string>();
        // The Roaming config's write fails, whatever the platform; the others are written as usual.
        ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, environment, BackupManager.DefaultKeepCount, reported.Add,
            ControlToolCopy.Remove, (servers, path) =>
            {
                if (path == RoamingConfig)
                {
                    throw new IOException("write refused");
                }
                ClaudeConfigIO.Write(servers, path);
            });
        Assert.Contains(reported, line => line.Contains(RoamingConfig));
        Assert.True(ClaudeConfigIO.ReadMcpServers(RoamingConfig).ContainsKey(ClaudeIntegration.EntryName));
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(MsixConfig));
        Assert.False(Directory.Exists(Bin));
        Assert.Equal(@"C:\Windows", environment.Path);
    }

    [Fact]
    public void ACopyThatThrowsDoesNotStopThePathStep()
    {
        Directory.CreateDirectory(Bin);
        var environment = new FakeEnvironment(@"C:\Windows;" + Bin);
        var reported = new List<string>();
        ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, environment, BackupManager.DefaultKeepCount, reported.Add,
            _ => throw new UnauthorizedAccessException("denied"));
        Assert.Contains("denied", Assert.Single(reported, line => line.Contains("copy")));
        Assert.Equal(@"C:\Windows", environment.Path);
    }

    [Fact]
    public void ACopyThatSurvivesIsReported()
    {
        Directory.CreateDirectory(Bin);
        File.WriteAllText(Tool, "tool");
        var reported = new List<string>();
        ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, new FakeEnvironment(null), BackupManager.DefaultKeepCount, reported.Add,
            _ => { });
        Assert.Contains("could not be deleted", Assert.Single(reported));
    }

    /// <summary>The app's record of itself goes first, before any config is changed: Velopack has already stopped
    /// the app, and a relay that gets a call meanwhile would start it again from that record.</summary>
    [Fact]
    public void TheAppsRecordGoesBeforeAnyConfigIsChanged()
    {
        Write(RoamingConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        Directory.CreateDirectory(Bin);
        var record = System.IO.Path.Combine(Bin, ControlEndpoint.AppPathFileName);
        File.WriteAllText(record, dir.File("current/ConnectorControl.exe"));
        var recordAtWrite = new List<bool>();
        // The copy step does nothing here, so only the record's own step can have deleted it.
        ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, new FakeEnvironment(null), BackupManager.DefaultKeepCount, null,
            _ => { }, (servers, path) =>
            {
                recordAtWrite.Add(File.Exists(record));
                ClaudeConfigIO.Write(servers, path);
            });
        Assert.Equal([false], recordAtWrite);
    }

    /// <summary>An app the relay started before the hook could stop it may put the entry back while the hook runs;
    /// the last step takes it away again.</summary>
    [Fact]
    public void AnEntryPutBackDuringTheUninstallIsTakenAwayAtTheEnd()
    {
        Write(RoamingConfig, ("github", Github), (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        var reported = new List<string>();
        ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, new FakeEnvironment(null), BackupManager.DefaultKeepCount, reported.Add,
            _ => Write(RoamingConfig, ("github", Github), (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool))));
        Assert.Equal(["github"], ClaudeConfigIO.ReadMcpServers(RoamingConfig).Keys);
        Assert.Empty(reported);
    }

    /// <summary>A record that cannot be deleted - held open on Windows, its folder read-only elsewhere - is reported,
    /// and the configs are still tidied.</summary>
    [Fact]
    public void ARecordThatCannotBeDeletedIsReportedAndTheConfigsStillGo()
    {
        Write(RoamingConfig, (ClaudeIntegration.EntryName, ClaudeIntegration.Entry(Tool)));
        Directory.CreateDirectory(Bin);
        var record = System.IO.Path.Combine(Bin, ControlEndpoint.AppPathFileName);
        File.WriteAllText(record, dir.File("current/ConnectorControl.exe"));
        var reported = new List<string>();
        using (OperatingSystem.IsWindows() ? new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Bin, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            try
            {
                ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, new FakeEnvironment(null), BackupManager.DefaultKeepCount, reported.Add);
            }
            finally
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(Bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }
        Assert.Contains(reported, line => line.StartsWith("The app's record could not be deleted", StringComparison.Ordinal));
        Assert.True(File.Exists(record));
        Assert.Empty(ClaudeConfigIO.ReadMcpServers(RoamingConfig));
    }

    [Fact]
    public void APathStepThatThrowsIsReported()
    {
        var reported = new List<string>();
        ClaudeIntegrationCleanup.Uninstall(Paths(), new PathOverrides(), DataDir, new ThrowingEnvironment(), BackupManager.DefaultKeepCount, reported.Add);
        Assert.Contains("PATH", Assert.Single(reported));
    }

    private sealed class ThrowingEnvironment : IUserEnvironment
    {
        public string? ReadPath() => throw new System.Security.SecurityException("no registry");
        public void WritePath(string value) => throw new System.Security.SecurityException("no registry");
    }
}
