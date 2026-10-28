namespace ConnectorControl.Core.State;

/// <summary>
/// The tools, in the order <c>tools/list</c> gives them and <c>help</c> groups them. A description is
/// documentation Claude reads, and its first sentence is the command's line in <c>help</c>. The hints
/// follow one rule: reads are read-only; the destructive writes are the ones that remove or
/// overwrite connectors; nothing reaches outside this machine; and only the three writes that leave
/// the same state however often they repeat say they are idempotent. Descriptions are plain
/// literals, never interpolated, so <c>${CC_NEEDS:NAME}</c> stays as written.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlCatalog.swift
/// </summary>
public sealed partial class ControlTools
{
    public const string StatusGroup = "Status";
    public const string ConnectorsGroup = "Connectors (the active collection unless --collection names another)";
    public const string CollectionsGroup = "Collections";
    public const string SharingGroup = "Sharing";
    public const string SettingsGroup = "Settings and backups";
    public const string ClaudeGroup = "Claude";
    public static readonly IReadOnlyList<string> HelpGroups = [StatusGroup, ConnectorsGroup, CollectionsGroup, SharingGroup, SettingsGroup, ClaudeGroup];

    // Everything from here to the catalog is declared before it because the catalog reads it as it
    // is built: C# runs static initializers in the order they are written.

    /// <summary>Each tool's title, as Claude's connector settings list it (sentence case, like the app's labels).</summary>
    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["get_status"] = "Get status", ["check_tools"] = "Check launchers", ["list_collections"] = "List collections",
        ["list_connectors"] = "List connectors", ["get_connector"] = "Show connector",
        ["get_pending_update"] = "Show pending update", ["list_backups"] = "List backups", ["get_settings"] = "Show settings",
        ["set_connector_enabled"] = "Turn connector on or off", ["switch_collection"] = "Switch collection",
        ["create_collection"] = "Create collection", ["rename_collection"] = "Rename collection",
        ["duplicate_collection"] = "Duplicate collection", ["refresh_subscription"] = "Refresh subscription",
        ["set_setting"] = "Change setting", ["add_connector"] = "Add connector", ["subscribe"] = "Subscribe to collection",
        ["restart_claude"] = "Restart Claude", ["update_connector"] = "Change connector",
        ["delete_connectors"] = "Delete connectors", ["delete_collection"] = "Delete collection",
        ["import_document"] = "Import collection", ["apply_update"] = "Apply update", ["restore_backup"] = "Restore backup",
    };

    internal const string CollectionOrActive = "A collection's name; the active collection when left out.";
    internal const string CollectionName = "The collection's name.";
    internal const string SubscribedName = "A subscribed collection's name.";
    internal const string ConnectorName = "The connector's name.";
    internal const string FilePath = "The collection file's full path.";
    internal static readonly IReadOnlyList<string> ClashChoices = ["replace", "keep-both", "skip"];

    public static readonly IReadOnlyList<ControlToolDefinition> Catalog =
    [
        Tool("get_status", ControlGateClass.Read, usage: "status", group: StatusGroup,
             description: "What Claude is running, and whether it needs a restart. Returns the active collection and how many of its connectors are on, whether Claude Desktop is running on an older config, whether Connector Control asks before Claude's changes, and where the master list is."),
        Tool("check_tools", ControlGateClass.Read, usage: "doctor", group: StatusGroup,
             description: "Whether npx, node, uvx and uv are installed where Claude can find them. A connector launched through a missing one shows only \"server disconnected\" in Claude."),
        Tool("list_connectors", ControlGateClass.Read, usage: "connectors [--collection <name>]", group: ConnectorsGroup,
             description: "List connectors and what each runs. Lists the active collection, the one Claude runs, unless collection names another. What each runs is a summary that never shows a secret.",
             properties: [("collection", Text(CollectionOrActive))]),
        Tool("get_connector", ControlGateClass.Read, usage: "show <name> [--reveal]", group: ConnectorsGroup,
             description: "Show one connector, with its secrets masked. Gives its command, arguments, URL and environment; environment and header values are never shown, only whether they are set. Looks in the active collection unless collection names another.",
             properties: [("name", Text(ConnectorName)), ("collection", Text(CollectionOrActive)),
                          ("reveal", Flag("The command-line tool only: show secret values. Ignored for Claude."))],
             required: ["name"]),
        Tool("set_connector_enabled", ControlGateClass.Direct, idempotent: true, usage: "enable <name> | disable <name>", group: ConnectorsGroup,
             description: "Turn a connector on or off. Acts on the active collection, the one Claude runs, as Connector Control's own switches do; Claude picks the change up when it restarts.",
             properties: [("name", Text("The connector's name, in the active collection.")),
                          ("enabled", Flag("true to turn it on, false to turn it off."))],
             required: ["name", "enabled"]),
        Tool("add_connector", ControlGateClass.Gated, usage: "add <name> --json <config> | --file <path>", group: ConnectorsGroup,
             description: "Add a connector. config is what Claude Desktop runs, as its config file holds it: {\"command\": ..., \"args\": [...], \"env\": {...}} for a local server, or an npx mcp-remote command line for a remote one. Never write a secret the user has not given you: put ${CC_NEEDS:NAME} in its place, and the user fills it in in Connector Control. The connector goes into the active collection unless collection names another, switched on unless enabled is false.",
             properties: [("name", Text("The new connector's name.")), ("config", Object("What Claude Desktop runs, as its config file holds it.")),
                          ("collection", Text(CollectionOrActive)), ("enabled", Flag("false to add it switched off."))],
             required: ["name", "config"]),
        Tool("update_connector", ControlGateClass.Gated, destructive: true, usage: "edit <name> --json <config> | --file <path> [--rename <new>]", group: ConnectorsGroup,
             description: "Change a connector's config, its name, or both. config replaces the whole config; write ${CC_NEEDS:NAME} for any secret the user has not given you. Subscribed collections are read-only.",
             properties: [("name", Text(ConnectorName)), ("config", Object("Its new config, replacing the old one whole.")),
                          ("newName", Text("A new name for it.")), ("collection", Text(CollectionOrActive))],
             required: ["name"]),
        Tool("delete_connectors", ControlGateClass.Gated, destructive: true, usage: "delete <name>…", group: ConnectorsGroup,
             description: "Delete connectors. A copy remains in Connector Control's backups.",
             properties: [("names", Names("The connectors' names.")), ("collection", Text(CollectionOrActive))],
             required: ["names"]),
        Tool("list_collections", ControlGateClass.Read, usage: "collections", group: CollectionsGroup,
             description: "List collections. Each is a set of connectors and the active one is what Claude runs; a subscribed collection follows a file someone else publishes and is read-only."),
        Tool("switch_collection", ControlGateClass.Direct, idempotent: true, usage: "use <collection>", group: CollectionsGroup,
             description: "Make a collection active. Claude runs its connectors after its next restart.",
             properties: [("collection", Text(CollectionName))], required: ["collection"]),
        Tool("create_collection", ControlGateClass.Direct, usage: "collection new <name>", group: CollectionsGroup,
             description: "Create an empty collection. It does not become active.",
             properties: [("name", Text("The new collection's name."))], required: ["name"]),
        Tool("rename_collection", ControlGateClass.Direct, usage: "collection rename <old> <new>", group: CollectionsGroup,
             description: "Rename a collection.",
             properties: [("collection", Text(CollectionName)), ("newName", Text("Its new name."))],
             required: ["collection", "newName"]),
        Tool("duplicate_collection", ControlGateClass.Direct, usage: "collection duplicate <name> <new>", group: CollectionsGroup,
             description: "Copy a collection under a new name. The copy holds the same connectors, each on or off as it is, and does not become active. A subscribed collection is copied in Connector Control instead.",
             properties: [("collection", Text("The collection to copy.")), ("newName", Text("The copy's name."))],
             required: ["collection", "newName"]),
        Tool("delete_collection", ControlGateClass.Gated, destructive: true, usage: "collection delete <name>", group: CollectionsGroup,
             description: "Delete a collection and its connectors. A copy remains in Connector Control's backups; a subscribed collection's file is left alone.",
             properties: [("collection", Text(CollectionName))], required: ["collection"]),
        Tool("import_document", ControlGateClass.Gated, destructive: true,
             usage: "import <file> [--into <collection> | --new <name>] [--on-clash replace|keep-both|skip]", group: SharingGroup,
             description: "Copy the connectors in a collection file into a collection. They arrive switched off, with no link to the file afterwards. onClash says what to do with a name the collection already has: replace it, keep both, or skip it.",
             properties: [("path", Text(FilePath)), ("into", Text("The collection the copies go into; the active one when left out.")),
                          ("newCollection", Text("A name for a new collection to put them in, instead of into.")),
                          ("onClash", Choice(ClashChoices, "What to do with a connector whose name is taken; skip when left out."))],
             required: ["path"]),
        Tool("subscribe", ControlGateClass.Gated, usage: "subscribe <file> [--name <name>]", group: SharingGroup,
             description: "Subscribe to a collection file someone publishes. It becomes a new, read-only collection that follows the file, and its connectors arrive switched off.",
             properties: [("path", Text(FilePath)), ("name", Text("The new collection's name; the file's own when left out."))],
             required: ["path"]),
        Tool("get_pending_update", ControlGateClass.Read, usage: "updates <collection>", group: SharingGroup,
             description: "Show what an update to a subscribed collection would change. Names the connectors its file adds, deletes and changes; apply_update lands it.",
             properties: [("collection", Text(SubscribedName))], required: ["collection"]),
        Tool("apply_update", ControlGateClass.Gated, destructive: true, usage: "apply-update <collection>", group: SharingGroup,
             description: "Apply the update waiting for a subscribed collection. get_pending_update shows what it changes.",
             properties: [("collection", Text(SubscribedName))], required: ["collection"]),
        Tool("refresh_subscription", ControlGateClass.Direct, usage: "refresh <collection>", group: SharingGroup,
             description: "Read a subscribed collection's file now. Says whether an update is waiting.",
             properties: [("collection", Text(SubscribedName))], required: ["collection"]),
        Tool("get_settings", ControlGateClass.Read, usage: "settings", group: SettingsGroup,
             description: "List Connector Control's settings and their values. The keys are the ones set_setting takes."),
        Tool("set_setting", ControlGateClass.Direct, idempotent: true, usage: "settings set <key> <value>", group: SettingsGroup,
             description: "Change a setting. Takes a key from get_settings and on or off, or a number from 5 to 100 for backups-to-keep. Only the user can change claude-integration and confirm-claude-changes.",
             properties: [("key", Choice(ControlSettingKey.All, "Which setting.")),
                          ("value", Text("on or off; for backups-to-keep, a number from 5 to 100."))],
             required: ["key", "value"]),
        Tool("list_backups", ControlGateClass.Read, usage: "backups", group: SettingsGroup,
             description: "List the backups of Claude's config. Each id can be given to restore_backup."),
        Tool("restore_backup", ControlGateClass.Gated, destructive: true, usage: "backups restore <id>", group: SettingsGroup,
             description: "Replace Claude's config with a backup. The current file is backed up first.",
             properties: [("id", Text("A backup's id, from list_backups."))], required: ["id"]),
        Tool("restart_claude", ControlGateClass.AlwaysConfirm, usage: "restart-claude", group: ClaudeGroup,
             description: "Quit and reopen Claude Desktop. The user is always asked first; the reply in progress stops and the conversation is kept."),

    ];

    /// <summary>The <c>tools/list</c> entries, built once: <c>tools/call</c> looks a tool up in them on every call.</summary>
    public static readonly IReadOnlyList<JsonValue> McpDescriptors = Catalog.Select(definition => definition.McpDescriptor).ToList().AsReadOnly();

    // Platform idiom: C# puts optional parameters last, so usage, group and description come before
    // the two hints here; every call names them, in the Mac's order.
    private static ControlToolDefinition Tool(string name, ControlGateClass gate, string usage, string group, string description,
                                              bool destructive = false, bool idempotent = false,
                                              (string Key, JsonValue Value)[]? properties = null, string[]? required = null)
    {
        var schema = JsonValue.Object(("type", JsonValue.String("object")), ("properties", JsonValue.Object(properties ?? [])));
        if (required is { Length: > 0 })
        {
            schema = schema.With("required", JsonValue.Array(required.Select(JsonValue.String)));
        }
        return new ControlToolDefinition(
            name, Titles.TryGetValue(name, out var title) ? title : name, description, schema,
            new ControlToolAnnotations(gate == ControlGateClass.Read, destructive, idempotent, false),
            gate, usage, group);
    }

    private static JsonValue Text(string description) =>
        JsonValue.Object(("type", JsonValue.String("string")), ("description", JsonValue.String(description)));

    private static JsonValue Flag(string description) =>
        JsonValue.Object(("type", JsonValue.String("boolean")), ("description", JsonValue.String(description)));

    private static JsonValue Names(string description) =>
        JsonValue.Object(("type", JsonValue.String("array")), ("items", JsonValue.Object(("type", JsonValue.String("string")))),
                         ("description", JsonValue.String(description)));

    private static JsonValue Object(string description) =>
        JsonValue.Object(("type", JsonValue.String("object")), ("description", JsonValue.String(description)));

    private static JsonValue Choice(IReadOnlyList<string> options, string description) =>
        JsonValue.Object(("type", JsonValue.String("string")), ("enum", JsonValue.Array(options.Select(JsonValue.String))),
                         ("description", JsonValue.String(description)));
}
