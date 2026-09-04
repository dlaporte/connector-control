using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// A real on-disk layout (%LOCALAPPDATA% and %APPDATA% under one temp dir),
/// the real path resolver, real ConfigService/FileWatcher, and fakes for the
/// platform interfaces only. Marshal is a queue: watcher callbacks reach state
/// only when a test pumps, mirroring the UI thread.
/// </summary>
public sealed class AppStateHarness : IDisposable
{
    public TempDir Dir { get; } = new("appstate");
    public string Local => Dir.File("Local");
    public string Roaming => Dir.File("Roaming");
    public string ClaudeConfigPath => Path.Combine(Roaming, "Claude", "claude_desktop_config.json");
    public string StoreDir => Path.Combine(Local, AppPathsResolver.DataDirName);
    public string MasterStorePath => Path.Combine(StoreDir, "mcps.json");
    public string BackupsDir => Path.Combine(StoreDir, "backups");

    public FakeSettings Settings { get; } = new();
    public FakeClaudeProcess Claude { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public DelayQueue Delays { get; } = new();
    public MarshalQueue Ui { get; } = new();
    public DateTime Now { get; set; } = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
    public PathContext Context { get; }
    public AppHost Host { get; }

    public AppStateHarness(bool seedClaudeConfig = true)
    {
        Directory.CreateDirectory(Path.Combine(Roaming, "Claude"));
        Directory.CreateDirectory(Local);
        if (seedClaudeConfig)
        {
            File.WriteAllText(ClaudeConfigPath, Fixtures.RealisticClaudeConfig);
        }
        Context = new PathContext(new Dictionary<string, string>(StringComparer.Ordinal), new KnownFolders(Local, Roaming), new RealPathProbe());
        Host = new AppHost(Ui.Post, Delays.Add, () => Now);
    }

    public AppState Create() => new(Settings, Claude, Notifier, Dialogs, Context, Host);

    public IReadOnlyDictionary<string, JsonValue> ClaudeServers() => ClaudeConfigIO.ReadMcpServers(ClaudeConfigPath);

    public MasterStore StoreOnDisk() => MasterStoreIO.Read(MasterStorePath) ?? throw new InvalidOperationException("no readable store on disk");

    /// <summary>An "external" edit of Claude's config: replaces mcpServers, keeps every other key.</summary>
    public void WriteClaudeServers(params (string Name, JsonValue Config)[] servers) =>
        ClaudeConfigIO.Write(servers.ToDictionary(s => s.Name, s => s.Config, StringComparer.Ordinal), ClaudeConfigPath);

    public static JsonValue Remote(string url) => JsonValue.Object(
        ("command", JsonValue.String("npx")),
        ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("mcp-remote"), JsonValue.String(url)])));

    public static string[] Keys(IEnumerable<string> keys) => keys.Order(StringComparer.Ordinal).ToArray();

    public void Dispose() => Dir.Dispose();
}
