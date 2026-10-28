import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlCatalog.cs
///
/// The tools, in the order `tools/list` gives them and `help` groups them. A description is
/// documentation Claude reads, and its first sentence is the command's line in `help`. The hints
/// follow one rule: reads are read-only; the destructive writes are the ones that remove or
/// overwrite connectors; nothing reaches outside this machine; and only the three writes that leave
/// the same state however often they repeat say they are idempotent.
extension ControlTools {
    public static let statusGroup = "Status"
    public static let connectorsGroup = "Connectors (the active collection unless --collection names another)"
    public static let collectionsGroup = "Collections"
    public static let sharingGroup = "Sharing"
    public static let settingsGroup = "Settings and backups"
    public static let claudeGroup = "Claude"
    public static let helpGroups = [statusGroup, connectorsGroup, collectionsGroup, sharingGroup, settingsGroup, claudeGroup]

    // Everything from here to the catalog is declared before it because the catalog reads it as it
    // is built: the Windows mirror runs static initializers in the order they are written.

    /// Each tool's title, as Claude's connector settings list it (sentence case, like the app's labels).
    public static let titles: [String: String] = [
        "get_status": "Get status", "check_tools": "Check launchers", "list_collections": "List collections",
        "list_connectors": "List connectors", "get_connector": "Show connector",
        "get_pending_update": "Show pending update", "list_backups": "List backups", "get_settings": "Show settings",
        "set_connector_enabled": "Turn connector on or off", "switch_collection": "Switch collection",
        "create_collection": "Create collection", "rename_collection": "Rename collection",
        "duplicate_collection": "Duplicate collection", "refresh_subscription": "Refresh subscription",
        "set_setting": "Change setting", "add_connector": "Add connector", "subscribe": "Subscribe to collection",
        "restart_claude": "Restart Claude", "update_connector": "Change connector",
        "delete_connectors": "Delete connectors", "delete_collection": "Delete collection",
        "import_document": "Import collection", "apply_update": "Apply update", "restore_backup": "Restore backup",
    ]

    static let collectionOrActive = "A collection's name; the active collection when left out."
    static let collectionName = "The collection's name."
    static let subscribedName = "A subscribed collection's name."
    static let connectorName = "The connector's name."
    static let filePath = "The collection file's full path."
    static let clashChoices = ["replace", "keep-both", "skip"]

    public static let catalog: [ControlToolDefinition] = [
        tool("get_status", .read, usage: "status", group: statusGroup,
             description: "What Claude is running, and whether it needs a restart. Returns the active collection and how many of its connectors are on, whether Claude Desktop is running on an older config, whether Connector Control asks before Claude's changes, and where the master list is."),
        tool("check_tools", .read, usage: "doctor", group: statusGroup,
             description: "Whether npx, node, uvx and uv are installed where Claude can find them. A connector launched through a missing one shows only \"server disconnected\" in Claude."),
        tool("list_connectors", .read, usage: "connectors [--collection <name>]", group: connectorsGroup,
             description: "List connectors and what each runs. Lists the active collection, the one Claude runs, unless collection names another. What each runs is a summary that never shows a secret.",
             properties: ["collection": text(collectionOrActive)]),
        tool("get_connector", .read, usage: "show <name> [--reveal]", group: connectorsGroup,
             description: "Show one connector, with its secrets masked. Gives its command, arguments, URL and environment; environment and header values are never shown, only whether they are set. Looks in the active collection unless collection names another.",
             properties: ["name": text(connectorName), "collection": text(collectionOrActive),
                          "reveal": flag("The command-line tool only: show secret values. Ignored for Claude.")],
             required: ["name"]),
        tool("set_connector_enabled", .direct, idempotent: true, usage: "enable <name> | disable <name>", group: connectorsGroup,
             description: "Turn a connector on or off. Acts on the active collection, the one Claude runs, as Connector Control's own switches do; Claude picks the change up when it restarts.",
             properties: ["name": text("The connector's name, in the active collection."),
                          "enabled": flag("true to turn it on, false to turn it off.")],
             required: ["name", "enabled"]),
        tool("add_connector", .gated, usage: "add <name> --json <config> | --file <path> [--off]", group: connectorsGroup,
             description: "Add a connector. config is what Claude Desktop runs, as its config file holds it: {\"command\": ..., \"args\": [...], \"env\": {...}} for a local server, or an npx mcp-remote command line for a remote one. Never write a secret the user has not given you: put ${CC_NEEDS:NAME} in its place, and the user fills it in in Connector Control. The connector goes into the active collection unless collection names another, switched on unless enabled is false.",
             properties: ["name": text("The new connector's name."), "config": object("What Claude Desktop runs, as its config file holds it."),
                          "collection": text(collectionOrActive), "enabled": flag("false to add it switched off.")],
             required: ["name", "config"]),
        tool("update_connector", .gated, destructive: true, usage: "edit <name> --json <config> | --file <path> [--rename <new>]", group: connectorsGroup,
             description: "Change a connector's config, its name, or both. config replaces the whole config; write ${CC_NEEDS:NAME} for any secret the user has not given you. Subscribed collections are read-only.",
             properties: ["name": text(connectorName), "config": object("Its new config, replacing the old one whole."),
                          "newName": text("A new name for it."), "collection": text(collectionOrActive)],
             required: ["name"]),
        tool("delete_connectors", .gated, destructive: true, usage: "delete <name>…", group: connectorsGroup,
             description: "Delete connectors. A copy remains in Connector Control's backups.",
             properties: ["names": names("The connectors' names."), "collection": text(collectionOrActive)],
             required: ["names"]),
        tool("list_collections", .read, usage: "collections", group: collectionsGroup,
             description: "List collections. Each is a set of connectors and the active one is what Claude runs; a subscribed collection follows a file someone else publishes and is read-only."),
        tool("switch_collection", .direct, idempotent: true, usage: "use <collection>", group: collectionsGroup,
             description: "Make a collection active. Claude runs its connectors after its next restart.",
             properties: ["collection": text(collectionName)], required: ["collection"]),
        tool("create_collection", .direct, usage: "collection new <name>", group: collectionsGroup,
             description: "Create an empty collection. It does not become active.",
             properties: ["name": text("The new collection's name.")], required: ["name"]),
        tool("rename_collection", .direct, usage: "collection rename <old> <new>", group: collectionsGroup,
             description: "Rename a collection.",
             properties: ["collection": text(collectionName), "newName": text("Its new name.")],
             required: ["collection", "newName"]),
        tool("duplicate_collection", .direct, usage: "collection duplicate <name> <new>", group: collectionsGroup,
             description: "Copy a collection under a new name. The copy holds the same connectors, each on or off as it is, and does not become active. A subscribed collection is copied in Connector Control instead.",
             properties: ["collection": text("The collection to copy."), "newName": text("The copy's name.")],
             required: ["collection", "newName"]),
        tool("delete_collection", .gated, destructive: true, usage: "collection delete <name>", group: collectionsGroup,
             description: "Delete a collection and its connectors. A copy remains in Connector Control's backups; a subscribed collection's file is left alone.",
             properties: ["collection": text(collectionName)], required: ["collection"]),
        tool("import_document", .gated, destructive: true,
             usage: "import <file> [--into <collection> | --new <name>] [--on-clash replace|keep-both|skip]", group: sharingGroup,
             description: "Copy the connectors in a collection file into a collection. They arrive switched off, with no link to the file afterwards. onClash says what to do with a name the collection already has: replace it, keep both, or skip it.",
             properties: ["path": text(filePath), "into": text("The collection the copies go into; the active one when left out."),
                          "newCollection": text("A name for a new collection to put them in, instead of into."),
                          "onClash": choice(clashChoices, "What to do with a connector whose name is taken; skip when left out.")],
             required: ["path"]),
        tool("subscribe", .gated, usage: "subscribe <file> [--name <name>]", group: sharingGroup,
             description: "Subscribe to a collection file someone publishes. It becomes a new, read-only collection that follows the file, and its connectors arrive switched off.",
             properties: ["path": text(filePath), "name": text("The new collection's name; the file's own when left out.")],
             required: ["path"]),
        tool("get_pending_update", .read, usage: "updates <collection>", group: sharingGroup,
             description: "Show what an update to a subscribed collection would change. Names the connectors its file adds, deletes and changes; apply_update lands it.",
             properties: ["collection": text(subscribedName)], required: ["collection"]),
        tool("apply_update", .gated, destructive: true, usage: "apply-update <collection>", group: sharingGroup,
             description: "Apply the update waiting for a subscribed collection. get_pending_update shows what it changes.",
             properties: ["collection": text(subscribedName)], required: ["collection"]),
        tool("refresh_subscription", .direct, usage: "refresh <collection>", group: sharingGroup,
             description: "Read a subscribed collection's file now. Says whether an update is waiting.",
             properties: ["collection": text(subscribedName)], required: ["collection"]),
        tool("get_settings", .read, usage: "settings", group: settingsGroup,
             description: "List Connector Control's settings and their values. The keys are the ones set_setting takes."),
        tool("set_setting", .direct, idempotent: true, usage: "settings set <key> <value>", group: settingsGroup,
             description: "Change a setting. Takes a key from get_settings and on or off, or a number from 5 to 100 for backups-to-keep. Only the user can change claude-integration and confirm-claude-changes.",
             properties: ["key": choice(ControlSettingKey.all, "Which setting."),
                          "value": text("on or off; for backups-to-keep, a number from 5 to 100.")],
             required: ["key", "value"]),
        tool("list_backups", .read, usage: "backups", group: settingsGroup,
             description: "List the backups of Claude's config. Each id can be given to restore_backup."),
        tool("restore_backup", .gated, destructive: true, usage: "backups restore <id>", group: settingsGroup,
             description: "Replace Claude's config with a backup. The current file is backed up first.",
             properties: ["id": text("A backup's id, from list_backups.")], required: ["id"]),
        tool("restart_claude", .alwaysConfirm, usage: "restart-claude", group: claudeGroup,
             description: "Quit and reopen Claude Desktop. The user is always asked first; the reply in progress stops and the conversation is kept."),
    ]

    /// The `tools/list` entries, built once: `tools/call` looks a tool up in them on every call.
    public static let mcpDescriptors: [JSONValue] = catalog.map(\.mcpDescriptor)

    // The Windows mirror declares usage, group and description before the two hints: C# puts
    // optional parameters last. Every call names them, in this order.
    static func tool(_ name: String, _ gate: ControlGateClass, destructive: Bool = false, idempotent: Bool = false,
                     usage: String, group: String, description: String,
                     properties: [String: JSONValue] = [:], required: [String] = []) -> ControlToolDefinition {
        var schema: [String: JSONValue] = ["type": .string("object"), "properties": .object(properties)]
        if !required.isEmpty { schema["required"] = .array(required.map(JSONValue.string)) }
        return ControlToolDefinition(
            name: name, title: ControlTools.titles[name] ?? name, description: description, inputSchema: .object(schema),
            annotations: ControlToolAnnotations(readOnly: gate == .read, destructive: destructive,
                                                idempotent: idempotent, openWorld: false),
            gate: gate, cliUsage: usage, cliGroup: group)
    }

    static func text(_ description: String) -> JSONValue {
        .object(["type": .string("string"), "description": .string(description)])
    }

    static func flag(_ description: String) -> JSONValue {
        .object(["type": .string("boolean"), "description": .string(description)])
    }

    static func names(_ description: String) -> JSONValue {
        .object(["type": .string("array"), "items": .object(["type": .string("string")]), "description": .string(description)])
    }

    static func object(_ description: String) -> JSONValue {
        .object(["type": .string("object"), "description": .string(description)])
    }

    static func choice(_ options: [String], _ description: String) -> JSONValue {
        .object(["type": .string("string"), "enum": .array(options.map(JSONValue.string)), "description": .string(description)])
    }
}
