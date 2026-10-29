namespace ConnectorControl.Core.State;

/// <summary>
/// The read tools. None of them writes anything: the store, the settings, Claude's config and the
/// backups are byte-identical after any of them.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlTools+Reads.swift
/// </summary>
public sealed partial class ControlTools
{
    public const string ActiveCollectionLabel = "Active collection";
    public const string ClaudeLabel = "Claude";
    public const string IntegrationLabel = "Claude integration";
    public const string MasterListLabel = "Master list";
    public const string ProblemLabel = "Problem";
    public const string ClaudeRunning = "running";
    public const string ClaudeRunningRestart = "running · restart required";
    public const string ClaudeNotRunning = "not running";
    public const string IntegrationOnConfirming = "on · confirmations on";
    public const string IntegrationOnNotConfirming = "on · confirmations off";
    public const string NameHeader = "NAME";
    public const string KindHeader = "KIND";
    public const string ConnectorsHeader = "CONNECTORS";
    public const string RunsHeader = "RUNS";
    public const string IdHeader = "ID";
    public const string CollectionHeader = "COLLECTION";
    public const string ActiveMark = "active";
    public const string LocalKind = "local";
    public const string SubscribedKind = "subscribed";
    public const string PublishedKind = "published";
    public const string UpdateAvailableKind = "update available";
    public const string NoBackupsText = "No backups yet.";
    public const string ClauseSeparator = "; ";

    public static string StatusTitle(string version) => $"Connector Control {version}";

    public static string ConnectorCountText(int total, int on) => $"{total} connectors, {on} on";

    public static string InstallHint(string title, string command) => $"{title}: {command}";

    public static string EmptyCollectionText(string collection) => $"“{collection}” has no connectors.";

    public static string NoConnectorError(string name, string collection) => $"There’s no connector named “{name}” in “{collection}”.";

    public static string NotSubscribedError(string collection) => $"“{collection}” isn’t a subscribed collection.";

    public static string UpToDateText(string collection) => $"“{collection}” is up to date.";

    public static string PendingUpdateText(string collection, string changes) => $"“{collection}” changed at its source: {changes}.";

    public static string AddsClause(string names) => $"adds {names}";

    public static string DeletesClause(string names) => $"deletes {names}";

    public static string ChangesClause(string names) => $"changes {names}";

    internal void Read(string name, ControlArguments arguments, ControlCaller caller, Action<ControlResult> done)
    {
        switch (name)
        {
            case "get_status":
                done(Status());
                break;
            case "check_tools":
                CheckTools(done);
                break;
            case "list_collections":
                done(ListCollections());
                break;
            case "list_connectors":
                done(ListConnectors(arguments.String("collection")));
                break;
            case "get_connector":
                // Secret values are the command-line tool's to show, never Claude's.
                done(Connector(arguments.String("name") ?? "", arguments.String("collection"),
                               reveal: arguments.Bool("reveal") == true && caller is ControlCaller.Cli));
                break;
            case "get_pending_update":
                done(PendingUpdate(arguments.String("collection") ?? ""));
                break;
            case "list_backups":
                done(ListBackups());
                break;
            case "get_settings":
                done(CurrentSettings());
                break;
            default:
                done(ControlResult.Refusal(UnknownToolError(name)));
                break;
        }
    }

    internal ControlResult Status()
    {
        var flags = state.Settings;
        var running = state.IsClaudeRunning;
        var claude = !running ? ClaudeNotRunning
            : state.NeedsClaudeRestart ? ClaudeRunningRestart : ClaudeRunning;
        var integration = !flags.ClaudeIntegrationEnabled ? OffWord
            : flags.ConfirmClaudeChanges ? IntegrationOnConfirming : IntegrationOnNotConfirming;
        var masterList = state.Service.Paths.MasterStorePath;
        var rows = new List<(string Label, string Value)>
        {
            (ActiveCollectionLabel,
             Detail(state.ActiveCollection, ConnectorCountText(state.Store.Mcps.Count, state.Store.EnabledServers.Count))),
            (ClaudeLabel, claude),
            (IntegrationLabel, integration),
            (MasterListLabel, masterList),
        };
        if (state.LastError is { } problem)
        {
            rows.Add((ProblemLabel, problem));
        }
        var structured = JsonValue.Object(
            ("version", JsonValue.String(Version)),
            ("activeCollection", JsonValue.String(state.ActiveCollection)),
            ("connectors", JsonValue.Int(state.Store.Mcps.Count)),
            ("enabled", JsonValue.Int(state.Store.EnabledServers.Count)),
            ("claude", JsonValue.Object(("running", JsonValue.Bool(running)), ("restartRequired", JsonValue.Bool(state.NeedsClaudeRestart)))),
            ("integration", JsonValue.Object(("enabled", JsonValue.Bool(flags.ClaudeIntegrationEnabled)),
                                             ("confirmChanges", JsonValue.Bool(flags.ConfirmClaudeChanges)))),
            ("masterList", JsonValue.String(masterList)),
            ("applyRetryNeeded", JsonValue.Bool(state.ApplyRetryNeeded)),
            ("problem", state.LastError is { } error ? JsonValue.String(error) : JsonValue.Null));
        return new ControlResult(structured,
            string.Join("\n", new[] { StatusTitle(Version) }.Concat(ControlText.Labelled(rows))),
            false);
    }

    /// <summary>Probes all four launchers again — the user may have installed one since — and answers once
    /// every result is in.</summary>
    internal void CheckTools(Action<ControlResult> done)
    {
        _ = state.RefreshToolsAsync();
        state.WhenToolsProbed(null, () => done(ToolsResult()));
    }

    /// <summary>Each launcher as the app's own words put it — "Found", "Not found" or, on the Mac, "Not visible
    /// to Claude Desktop"; never a version, so the column reads the same whatever is installed — with
    /// where it is or how to install it.</summary>
    internal ControlResult ToolsResult()
    {
        var rows = new List<IReadOnlyList<string>>();
        var items = new List<JsonValue>();
        foreach (var tool in ToolInfo.All)
        {
            state.ToolStatuses.TryGetValue(tool, out var status);
            var item = new List<(string Key, JsonValue Value)> { ("name", JsonValue.String(ToolInfo.Name(tool))) };
            string word;
            string detail;
            // The Windows probe has no shell-only state; the Mac's reads "shellOnly" with its path.
            if (status is { Found: true, Path: { } path })
            {
                item.Add(("status", JsonValue.String("found")));
                item.Add(("path", JsonValue.String(path)));
                word = ToolNote.FoundText;
                detail = path;
            }
            else
            {
                item.Add(("status", JsonValue.String("notFound")));
                word = ToolNote.NotFoundText;
                var family = ToolInfo.Family(tool);
                detail = InstallHint(ToolInfo.LinkTitle(family), ToolInfo.InstallCommand(family));
            }
            rows.Add([ToolInfo.Name(tool), word, detail]);
            items.Add(JsonValue.Object(item.ToArray()));
        }
        return new ControlResult(JsonValue.Object(("tools", JsonValue.Array(items))),
            string.Join("\n", ControlText.Table(null, rows)), false);
    }

    internal ControlResult ListCollections()
    {
        var rows = new List<IReadOnlyList<string>>();
        var items = new List<JsonValue>();
        foreach (var name in state.CollectionNames)
        {
            var mcps = state.Store.Collections.TryGetValue(name, out var held) ? held.Mcps : new Dictionary<string, McpEntry>();
            var enabled = mcps.Values.Count(entry => entry.Enabled);
            var synced = state.IsSynced(name);
            var published = state.IsPublished(name);
            var pending = state.PendingUpdates.ContainsKey(name);
            var kind = synced ? SubscribedKind : LocalKind;
            if (published)
            {
                kind = Detail(kind, PublishedKind);
            }
            if (pending)
            {
                kind = Detail(kind, UpdateAvailableKind);
            }
            var count = ConnectorCountText(mcps.Count, enabled);
            rows.Add([name == state.ActiveCollection ? ActiveMark : "", name, kind, count]);
            items.Add(JsonValue.Object(
                ("name", JsonValue.String(name)),
                ("active", JsonValue.Bool(name == state.ActiveCollection)),
                ("kind", JsonValue.String(synced ? "subscribed" : "local")),
                ("published", JsonValue.Bool(published)),
                ("updateAvailable", JsonValue.Bool(pending)),
                ("connectors", JsonValue.Int(mcps.Count)),
                ("enabled", JsonValue.Int(enabled))));
        }
        string[] header = ["", NameHeader, KindHeader, ConnectorsHeader];
        return new ControlResult(
            JsonValue.Object(("active", JsonValue.String(state.ActiveCollection)), ("collections", JsonValue.Array(items))),
            string.Join("\n", ControlText.Table(header, rows)), false);
    }

    internal ControlResult ListConnectors(string? requested)
    {
        var collection = requested ?? state.ActiveCollection;
        var shown = ControlText.EscapedName(collection);
        if (!state.Store.Collections.TryGetValue(collection, out var held))
        {
            return ControlResult.Refusal(MasterStore.NoCollectionError(shown));
        }
        var rows = new List<IReadOnlyList<string>>();
        var items = new List<JsonValue>();
        foreach (var name in held.Mcps.Keys.Order(StringComparer.Ordinal))
        {
            var entry = held.Mcps[name];
            var runs = CollectionsModel.TargetOf(entry.Config);
            rows.Add([entry.Enabled ? OnWord : OffWord, name, runs]);
            items.Add(JsonValue.Object(
                ("name", JsonValue.String(name)),
                ("enabled", JsonValue.Bool(entry.Enabled)),
                ("runs", JsonValue.String(runs)),
                ("caution", state.ConnectorCaution(name, collection) is { } caution ? JsonValue.String(caution) : JsonValue.Null)));
        }
        var text = rows.Count == 0
            ? EmptyCollectionText(shown)
            : string.Join("\n", ControlText.Table(["", NameHeader, RunsHeader], rows));
        return new ControlResult(JsonValue.Object(("collection", JsonValue.String(collection)), ("connectors", JsonValue.Array(items))),
            text, false);
    }

    internal ControlResult Connector(string name, string? requested, bool reveal)
    {
        var collection = requested ?? state.ActiveCollection;
        var shownName = ControlText.EscapedName(name);
        var shownCollection = ControlText.EscapedName(collection);
        if (!state.Store.Collections.TryGetValue(collection, out var held))
        {
            return ControlResult.Refusal(MasterStore.NoCollectionError(shownCollection));
        }
        if (!held.Mcps.TryGetValue(name, out var entry))
        {
            return ControlResult.Refusal(NoConnectorError(shownName, shownCollection));
        }
        var heading = Detail(Detail(name, collection), entry.Enabled ? OnWord : OffWord);
        var structured = JsonValue.Object(
            ("name", JsonValue.String(name)),
            ("collection", JsonValue.String(collection)),
            ("enabled", JsonValue.Bool(entry.Enabled)),
            ("config", ControlMasking.MaskedConfig(entry.Config, reveal)),
            ("needs", JsonValue.Array(Placeholder.UnfilledNamesIn(entry.Config).Select(JsonValue.String))),
            ("caution", state.ConnectorCaution(name, collection) is { } caution ? JsonValue.String(caution) : JsonValue.Null));
        return new ControlResult(structured,
            string.Join("\n", new[] { heading }.Concat(ControlMasking.CodeLines(entry.Config,
                reveal ? ControlMasking.Exposure.Revealed : ControlMasking.Exposure.Masked))),
            false);
    }

    /// <summary>What an update adds, deletes and changes, each name quoted through <see cref="ControlText.QuotedList"/>
    /// — the document's author names its connectors — so that none can invent a clause of its own.
    /// The window's banner, written for names read beside a list, joins them bare.</summary>
    internal static string ChangesText(CollectionDiff diff)
    {
        var clauses = new List<string>();
        if (diff.Added.Count > 0)
        {
            clauses.Add(AddsClause(ControlText.QuotedList(diff.Added)));
        }
        if (diff.Removed.Count > 0)
        {
            clauses.Add(DeletesClause(ControlText.QuotedList(diff.Removed)));
        }
        if (diff.Changed.Count > 0)
        {
            clauses.Add(ChangesClause(ControlText.QuotedList(diff.Changed)));
        }
        return string.Join(ClauseSeparator, clauses);
    }

    /// <summary>What an update would change: the window's banner, with the collection quoted and every name
    /// through <see cref="ControlText.EscapedName"/>, since others chose them. refresh_subscription answers
    /// with it, and is announced with it when it lands late; apply_update asks with it.</summary>
    internal ControlResult PendingUpdate(string collection)
    {
        var shown = ControlText.EscapedName(collection);
        if (!state.Store.Collections.ContainsKey(collection))
        {
            return ControlResult.Refusal(MasterStore.NoCollectionError(shown));
        }
        if (!state.IsSynced(collection))
        {
            return ControlResult.Refusal(NotSubscribedError(shown));
        }
        if (state.SourceErrors.TryGetValue(collection, out var problem))
        {
            return ControlResult.Refusal(problem);
        }
        var diff = state.PendingUpdates.GetValueOrDefault(collection);
        var structured = JsonValue.Object(
            ("collection", JsonValue.String(collection)),
            ("added", JsonValue.Array((diff?.Added ?? []).Select(JsonValue.String))),
            ("removed", JsonValue.Array((diff?.Removed ?? []).Select(JsonValue.String))),
            ("changed", JsonValue.Array((diff?.Changed ?? []).Select(JsonValue.String))));
        var text = diff is not null
            ? PendingUpdateText(shown, ChangesText(diff))
            : UpToDateText(shown);
        return new ControlResult(structured, text, false);
    }

    /// <summary>The backups the Restore sheet lists: newest first, the first-run original last.</summary>
    internal List<string> BackupPaths() => new(state.Service.Backups.RestorableBackups(RestoreModel.Series));

    internal ControlResult ListBackups()
    {
        List<string> paths;
        try
        {
            paths = BackupPaths();
        }
        catch (Exception error)
        {
            return ControlResult.Refusal(AppState.Friendly(error));
        }
        var backupsDir = state.Service.Paths.BackupsDir;
        var rows = new List<IReadOnlyList<string>>();
        var items = new List<JsonValue>();
        foreach (var path in paths)
        {
            var collection = BackupCollections.CollectionOf(path, backupsDir);
            rows.Add([Path.GetFileName(path), collection ?? ""]);
            items.Add(JsonValue.Object(("id", JsonValue.String(Path.GetFileName(path))),
                                       ("collection", collection is not null ? JsonValue.String(collection) : JsonValue.Null)));
        }
        var text = rows.Count == 0
            ? NoBackupsText
            : string.Join("\n", ControlText.Table([IdHeader, CollectionHeader], rows));
        return new ControlResult(JsonValue.Object(("backups", JsonValue.Array(items))), text, false);
    }

    internal ControlResult CurrentSettings()
    {
        // Launch at login is read fresh, as the General tab reads it whenever it appears: the user
        // may have changed it outside the app since.
        settings.Refresh();
        var values = new List<(string Key, JsonValue Value)>();
        var rows = new List<(string Label, string Value)>();
        foreach (var key in ControlSettingKey.All)
        {
            var value = SettingValue(key);
            values.Add((key, value));
            rows.Add((key, SettingText(value)));
        }
        return new ControlResult(JsonValue.Object(("settings", JsonValue.Object(values.ToArray()))),
            string.Join("\n", ControlText.Labelled(rows)), false);
    }

    internal JsonValue SettingValue(string key) => key switch
    {
        // LaunchAtStartup is the Windows name for launch at login.
        ControlSettingKey.LaunchAtLogin => JsonValue.Bool(settings.LaunchAtStartup),
        ControlSettingKey.ConfirmRestart => JsonValue.Bool(settings.ConfirmBeforeRestart),
        ControlSettingKey.ConfirmQuit => JsonValue.Bool(settings.ConfirmBeforeQuit),
        ControlSettingKey.NotifyExternalChanges => JsonValue.Bool(settings.NotifyExternalChanges),
        ControlSettingKey.AutomaticUpdates => JsonValue.Bool(settings.AutoUpdate),
        ControlSettingKey.BackupsToKeep => JsonValue.Int(settings.BackupKeepCount),
        ControlSettingKey.ClaudeIntegration => JsonValue.Bool(settings.ClaudeIntegrationEnabled),
        ControlSettingKey.ConfirmClaudeChanges => JsonValue.Bool(settings.ConfirmClaudeChanges),
        _ => JsonValue.Null,
    };

    internal static string SettingText(JsonValue value) => value.Kind switch
    {
        JsonKind.Bool => value.BoolValue ? OnWord : OffWord,
        JsonKind.Int => value.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "",
    };
}
