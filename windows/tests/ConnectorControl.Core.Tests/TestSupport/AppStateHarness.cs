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
    public string StoreDir => Path.Combine(Local, AppPaths.DataDirName);
    public string MasterStorePath => Path.Combine(StoreDir, "mcps.json");
    public string BackupsDir => Path.Combine(StoreDir, "backups");

    public FakeSettings Settings { get; } = new();
    public FakeClaudeProcess Claude { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeToolProbe Tools { get; } = new();
    public DelayQueue Delays { get; } = new();
    public MarshalQueue Ui { get; } = new();
    private readonly List<CollectionsModel> models = [];
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

    public AppState Create() => new(Settings, Claude, Notifier, Dialogs, Context, Host, Tools);

    public IReadOnlyDictionary<string, JsonValue> ClaudeServers() => ClaudeConfigIO.ReadMcpServers(ClaudeConfigPath);

    public MasterStore StoreOnDisk() => MasterStoreIO.Read(MasterStorePath) ?? throw new InvalidOperationException("no readable store on disk");

    /// <summary>An "external" edit of Claude's config: replaces mcpServers, keeps every other key.</summary>
    public void WriteClaudeServers(params (string Name, JsonValue Config)[] servers) =>
        ClaudeConfigIO.Write(servers.ToDictionary(s => s.Name, s => s.Config, StringComparer.Ordinal), ClaudeConfigPath);

    public static JsonValue Remote(string url) => RemotePattern.Make(url, RemoteLaunchStyle.Npx);

    /// <summary>A local connector running <paramref name="command"/> with <paramref name="args"/>.</summary>
    public static McpEntry LocalConnector(string command, params string[] args) =>
        new(JsonValue.Object(
            ("command", JsonValue.String(command)),
            ("args", JsonValue.Array(args.Select(JsonValue.String)))));

    public static string[] Keys(IEnumerable<string> keys) => keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Claude running since <paramref name="hours"/> before the harness clock, so the next
    /// apply calls for a restart.</summary>
    public void ClaudeRunningSince(double hours)
    {
        Claude.IsRunning = true;
        Claude.LaunchDate = Now.AddHours(-hours);
    }

    // MARK: collections

    /// <summary>The bytes an author's machine would have written, at <paramref name="name"/> under
    /// the temp dir.</summary>
    public string WriteDocument(CollectionDocument doc, string name)
    {
        var path = Dir.File(name);
        WriteDocumentAt(doc, path);
        return path;
    }

    /// <summary>The same, at a path the test already holds: the author's next commit to a
    /// document.</summary>
    public static void WriteDocumentAt(CollectionDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, doc.Serialize());
    }

    /// <summary>Writes <paramref name="doc"/> at <paramref name="name"/> under the temp dir and
    /// subscribes <paramref name="state"/> to it, as <paramref name="collection"/> or under the
    /// document's own name; a refusal fails the test. Returns where the document is.</summary>
    public string Subscribe(AppState state, CollectionDocument doc, string name = "data-team.json", string? collection = null)
    {
        var path = WriteDocument(doc, name);
        Assert.Null(state.Subscribe(path, collection));
        return path;
    }

    /// <summary>Writes both collection files where the app reads them, then reloads so the state
    /// picks them up — the shape a subscribe or a publish would leave behind.</summary>
    public void Seed(AppState state, CollectionsFile file, CollectionsLocalCache? cache = null)
    {
        file.Save(Path.Combine(StoreDir, CollectionsFile.FileName));
        (cache ?? new CollectionsLocalCache([], [])).Save(state.Service.Paths.CollectionsCachePath);
        state.Reload();
    }

    /// <summary>Marks <paramref name="name"/>, already in the store, synced from
    /// <c>&lt;slug&gt;.json</c>: found at <paramref name="boundTo"/> on this machine or, with null,
    /// not found here. It is the only collection the sidecar describes and the only synced binding
    /// in the cache; the rest of the cache is kept.</summary>
    public void MakeSynced(AppState state, string name, string? boundTo = null)
    {
        var entry = new CollectionsFile.Entry(CollectionKind.Synced, Slug.Make(name) + ".json");
        var bindings = new Dictionary<string, CollectionsLocalCache.SyncedBinding>(StringComparer.Ordinal);
        if (boundTo is not null)
        {
            bindings[name] = new CollectionsLocalCache.SyncedBinding(boundTo, null, []);
        }
        Seed(state, new CollectionsFile([new(name, entry)]), state.CollectionsCache with { Synced = bindings });
    }

    /// <summary>Publishes <paramref name="collection"/> into a new folder <paramref name="folder"/>
    /// under the temp dir; a refusal fails the test. Returns the document it wrote.</summary>
    public string Publish(AppState state, string collection, PublishIntent? intent = null, string folder = "pub")
    {
        var path = Dir.File(folder);
        Directory.CreateDirectory(path);
        Assert.Null(state.StartPublishing(collection, path, intent ?? PublishIntent.None));
        return Path.Combine(path, CollectionDocument.FileName(Slug.Make(collection)));
    }

    /// <summary>Reads the store off disk, lets <paramref name="edit"/> change it and saves it back:
    /// a write from another machine or an older app, which the running state has not seen.</summary>
    public void EditStoreOnDisk(Action<MasterStore> edit)
    {
        var store = StoreOnDisk();
        edit(store);
        MasterStoreIO.Save(store, MasterStorePath);
    }

    /// <summary>A Collections window's model on <paramref name="state"/>, showing
    /// <paramref name="selecting"/> (the active collection when null) with
    /// <paramref name="ticking"/> ticked. The harness disposes it.</summary>
    public CollectionsModel CollectionsModel(AppState state, string? selecting = null, params string[] ticking)
    {
        var model = new CollectionsModel(state, Dialogs);
        models.Add(model);
        if (selecting is not null)
        {
            model.Selected = selecting;
        }
        foreach (var name in ticking)
        {
            model.SetChecked(name, true);
        }
        return model;
    }

    public void Dispose()
    {
        foreach (var model in models)
        {
            model.Dispose();
        }
        models.Clear();
        Dir.Dispose();
    }
}
