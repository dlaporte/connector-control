using System.Text.Json;

namespace ConnectorControl.Core.State;

/// <summary>
/// The writes that change what a connector runs, or what Claude could run: connectors, deleting a
/// collection, importing, subscribing, applying an update, restoring a backup, and the restart.
/// Each is prepared in full before anyone is asked: every refusal, then the question — who is
/// asking, for what, and exactly what would run, with secrets masked — then the change itself,
/// which runs only on Allow (or with confirmations off, or for the command-line tool; never
/// unasked for the restart Claude asks for) and is announced when nobody was asked.
///
/// The question, the answer and the announcement name connectors, collections and the asker as
/// others chose them — Claude names connectors, creates and renames collections without asking, and
/// any client names itself — so every name goes through <see cref="ControlText.EscapedName"/>, and the asker
/// arrives as <see cref="ControlCaller.ShownAsker"/>, escaped and framed already: none can break a line, reorder
/// or hide text, or close its own quotes.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlTools+Gated.swift
/// </summary>
public sealed partial class ControlTools
{
    public const string TakesEffectFooter = "Takes effect when Claude restarts.";
    public const string ConfigShapeError = "A connector’s config needs a “command” or a “url”.";
    public const string NothingToChangeError = "Give a new config, a new name, or both.";

    public static string AddTitle(string shownAsker, string name) => $"{shownAsker} wants to add “{name}”";

    public static string AddActiveMessage(string collection, string state) => $"It would join “{collection}”, the collection Claude is running, switched {state}, and run:";

    public static string AddMessage(string collection, string state) => $"It would join “{collection}”, switched {state}, and run:";

    public static string ChangeTitle(string shownAsker, string name) => $"{shownAsker} wants to change “{name}”";

    public static string ChangeActiveMessage(string collection) => $"In “{collection}”, the collection Claude is running:";

    public static string ChangeMessage(string collection) => $"In “{collection}”:";

    public static string DeleteTitle(string shownAsker, string names) => $"{shownAsker} wants to delete {names}";

    public static string DeleteActiveMessage(string collection) => $"From “{collection}”, the collection Claude is running. A copy remains in Backups.";

    public static string DeleteMessage(string collection) => $"From “{collection}”. A copy remains in Backups.";

    public static string AddedText(string name, string collection, string state) => $"Added “{name}” to “{collection}”, switched {state}.";

    public static string NeedsValuesText(string names) => $"It needs a value for {names}: fill it in Connector Control.";

    public static string ChangedText(string name, string collection) => $"Changed “{name}” in “{collection}”.";

    public static string RenamedText(string old, string @new) => $"Renamed “{old}” to “{@new}”.";

    public static string DeletedText(string names, string collection) => $"Deleted {names} from “{collection}”.";

    public static string UnchangedError(string name) => $"“{name}” already has that config.";

    public static string AddedNotification(string shownAsker, string name, string collection) => $"{shownAsker} added “{name}” to “{collection}”.";

    public static string ChangedNotification(string shownAsker, string name, string collection) => $"{shownAsker} changed “{name}” in “{collection}”.";

    public static string RenamedNotification(string shownAsker, string old, string @new) => $"{shownAsker} renamed “{old}” to “{@new}”.";

    public static string DeletedNotification(string shownAsker, string names, string collection) => $"{shownAsker} deleted {names} from “{collection}”.";

    /// <summary>What the editor's JSON view refuses on Save, for a config that arrives as JSON: anything but
    /// an object with a <c>command</c> or a <c>url</c>, and a canonical mcp-remote line without a valid URL.
    /// This mirror also refuses what the <c>cmd /c</c> launcher would hand to cmd.exe; a Mac never
    /// writes that launcher.</summary>
    internal static string? ConfigError(JsonValue config)
    {
        if (config.Kind != JsonKind.Object)
        {
            return EditorModel.NotValidJson;
        }
        var runnable = false;
        if (config["command"] is { Kind: JsonKind.String } command && command.StringValue.TrimSpaces().Length > 0)
        {
            runnable = true;
        }
        if (config["url"] is { Kind: JsonKind.String } url && url.StringValue.TrimSpaces().Length > 0)
        {
            runnable = true;
        }
        if (!runnable)
        {
            return ConfigShapeError;
        }
        if (RemotePattern.IsCanonicalShape(config) && RemotePattern.Detect(config) is null)
        {
            return EditorModel.InvalidUrlError;
        }
        // The cmd /c launcher hands these fields to cmd.exe, which treats & | < > ^ " and spaces as
        // commands; the Mac never writes that launcher, so its mirror has no such check.
        if (RemotePattern.Decode(config) is { LaunchStyle: RemoteLaunchStyle.CmdNpx } remote
            && RemotePattern.CmdUnsafeField(remote) is { } field)
        {
            return RemotePattern.CmdUnsafeReason(field);
        }
        return null;
    }

    // Platform idiom: JsonValue already compares text ordinally, as the Mac mirror compares it scalar by
    // scalar; Swift's == would hold canonically equivalent text equal and refuse a change as no change.
    /// <summary>Whether two configs are the same, value for value.</summary>
    internal static bool Identical(JsonValue one, JsonValue other) => one == other;

    /// <summary>The collection a connector tool works in: the one named, else the active one.</summary>
    internal string TargetCollection(ControlArguments arguments) => arguments.String("collection") ?? state.ActiveCollection;

    private ControlStep AddConnector(ControlArguments arguments, string shownAsker)
    {
        var collection = TargetCollection(arguments);
        var shownCollection = ControlText.EscapedName(collection);
        if (state.Store.Collections.GetValueOrDefault(collection) is not { } held)
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(shownCollection));
        }
        if (state.IsSynced(collection))
        {
            return new ControlStep.Refused(CollectionsModel.AddConnectorDisabledTooltip);
        }
        var name = (arguments.String("name") ?? "").TrimSpaces();
        var shownName = ControlText.EscapedName(name);
        if (name.Length == 0)
        {
            return new ControlStep.Refused(AppState.NameEmptyError);
        }
        if (name == ClaudeIntegration.EntryName)
        {
            return new ControlStep.Refused(ClaudeIntegration.ReservedNameError);
        }
        if (held.Mcps.ContainsKey(name))
        {
            return new ControlStep.Refused(AppState.DuplicateNameError(shownName));
        }
        var config = arguments.Object("config") ?? JsonValue.Object();
        if (ConfigError(config) is { } error)
        {
            return new ControlStep.Refused(error);
        }
        var on = arguments.Bool("enabled") ?? true;
        var word = on ? OnWord : OffWord;
        var active = collection == state.ActiveCollection;
        var request = new ConfirmationRequest(
            AddTitle(shownAsker, shownName),
            active ? AddActiveMessage(shownCollection, word) : AddMessage(shownCollection, word),
            ControlMasking.CodeLines(config, ControlMasking.Exposure.Dialog),
            active && on ? TakesEffectFooter : null);
        return new ControlStep.Ready(request, () =>
        {
            var mark = state.AppliedServers;
            if (state.Upsert(name, new McpEntry(on, config, EditView.Json), renamedFrom: null, collection: collection) is { } refusal)
            {
                return Refused(refusal, [name, collection]);
            }
            state.ApplyInteractively();
            var needs = Placeholder.UnfilledNamesIn(config);
            var lines = new List<string> { AddedText(shownName, shownCollection, word) };
            if (needs.Count > 0)
            {
                lines.Add(NeedsValuesText(ControlText.QuotedList(needs)));
            }
            return Done(lines, [("collection", JsonValue.String(collection)), ("name", JsonValue.String(name)), ("enabled", JsonValue.Bool(on)),
                                ("needs", JsonValue.Array(needs.Select(JsonValue.String)))],
                collection, mark, RestartToUseText,
                notification: AddedNotification(shownAsker, shownName, shownCollection));
        });
    }

    private ControlStep UpdateConnector(ControlArguments arguments, string shownAsker)
    {
        var collection = TargetCollection(arguments);
        var shownCollection = ControlText.EscapedName(collection);
        if (state.Store.Collections.GetValueOrDefault(collection) is not { } held)
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(shownCollection));
        }
        if (state.IsSynced(collection))
        {
            return new ControlStep.Refused(CollectionsModel.LockedGlyphTooltip);
        }
        var name = arguments.String("name") ?? "";
        var shownName = ControlText.EscapedName(name);
        if (held.Mcps.GetValueOrDefault(name) is not { } current)
        {
            return new ControlStep.Refused(NoConnectorError(shownName, shownCollection));
        }
        var config = arguments.Object("config");
        var requestedName = arguments.String("newName");
        if (config is null && requestedName is null)
        {
            return new ControlStep.Refused(NothingToChangeError);
        }
        var newName = requestedName?.TrimSpaces() ?? name;
        var shownNewName = ControlText.EscapedName(newName);
        if (newName.Length == 0)
        {
            return new ControlStep.Refused(AppState.NameEmptyError);
        }
        var renamed = newName != name;
        if (renamed && newName == ClaudeIntegration.EntryName)
        {
            return new ControlStep.Refused(ClaudeIntegration.ReservedNameError);
        }
        if (renamed && held.Mcps.ContainsKey(newName))
        {
            return new ControlStep.Refused(AppState.DuplicateNameError(shownNewName));
        }
        if (config is not null && ConfigError(config) is { } error)
        {
            return new ControlStep.Refused(error);
        }
        var after = config ?? current.Config;
        // "Unchanged" compares the configs themselves, not the rendered lines: every difference is a
        // change, whether or not a line can show it.
        var reconfigured = !Identical(after, current.Config);
        if (!renamed && !reconfigured)
        {
            return new ControlStep.Refused(UnchangedError(shownName));
        }
        var changes = ControlMasking.ChangeLines(current.Config, after, ControlMasking.Exposure.Dialog);
        var active = collection == state.ActiveCollection;
        IReadOnlyList<string> names = renamed ? [ControlMasking.NameLabel, ControlMasking.RemovedPrefix + shownName, ControlMasking.AddedPrefix + shownNewName] : [];
        var request = new ConfirmationRequest(
            ChangeTitle(shownAsker, shownName),
            active ? ChangeActiveMessage(shownCollection) : ChangeMessage(shownCollection),
            [.. names, .. changes],
            active && current.Enabled ? TakesEffectFooter : null);
        return new ControlStep.Ready(request, () =>
        {
            var mark = state.AppliedServers;
            if (state.Upsert(newName, new McpEntry(current.Enabled, after, current.LastEditView), renamedFrom: name, collection: collection)
                is { } refusal)
            {
                return Refused(refusal, [newName, name, collection]);
            }
            state.ApplyInteractively();
            var lines = new List<string>();
            if (renamed)
            {
                lines.Add(RenamedText(shownName, shownNewName));
            }
            if (reconfigured)
            {
                lines.Add(ChangedText(shownNewName, shownCollection));
            }
            return Done(lines, [("collection", JsonValue.String(collection)), ("name", JsonValue.String(newName)), ("previousName", JsonValue.String(name))],
                collection, mark, RestartToApplyText,
                // A rename and nothing else is announced as one; any other change by what it changed.
                notification: reconfigured ? ChangedNotification(shownAsker, shownNewName, shownCollection)
                                           : RenamedNotification(shownAsker, shownName, shownNewName));
        });
    }

    private ControlStep DeleteConnectors(ControlArguments arguments, string shownAsker)
    {
        var collection = TargetCollection(arguments);
        var shownCollection = ControlText.EscapedName(collection);
        if (state.Store.Collections.GetValueOrDefault(collection) is not { } held)
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(shownCollection));
        }
        if (state.IsSynced(collection))
        {
            return new ControlStep.Refused(CollectionsModel.LockedGlyphTooltip);
        }
        var names = new List<string>();
        foreach (var name in arguments.Strings("names") ?? [])
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }
        if (names.Count == 0)
        {
            return new ControlStep.Refused(MissingArgumentError("names"));
        }
        // All or nothing: a list with one wrong name is a mistake, not a request for the rest.
        if (names.FirstOrDefault(name => !held.Mcps.ContainsKey(name)) is { } missing)
        {
            return new ControlStep.Refused(NoConnectorError(ControlText.EscapedName(missing), shownCollection));
        }
        var listed = ControlText.QuotedList(names);
        var active = collection == state.ActiveCollection;
        var reachesClaude = active && names.Any(name => held.Mcps.GetValueOrDefault(name)?.Enabled == true);
        var request = new ConfirmationRequest(
            DeleteTitle(shownAsker, listed),
            active ? DeleteActiveMessage(shownCollection) : DeleteMessage(shownCollection),
            [], reachesClaude ? TakesEffectFooter : null);
        return new ControlStep.Ready(request, () =>
        {
            var mark = state.AppliedServers;
            state.Delete(names, collection);
            state.ApplyInteractively();
            return Done([DeletedText(listed, shownCollection)],
                [("collection", JsonValue.String(collection)), ("names", JsonValue.Array(names.Select(JsonValue.String)))],
                collection, mark, RestartToApplyText,
                notification: DeletedNotification(shownAsker, listed, shownCollection));
        });
    }

    // MARK: Collections, sharing, backups and restart

    public const string PublishedFileStaysText = "Its published file stays in its folder.";
    public const string RestartItselfTitle = "Claude wants to restart itself";
    public const string RestartMessage = "Claude Desktop will quit and reopen to pick up the change. The reply in progress stops; the conversation is kept.";
    public const string RestartingText = "Restarting Claude Desktop.";
    public const string IntoOrNewCollectionError = "Give “into” or “newCollection”, not both.";
    public const string NetworkPathError = "Only the command-line tool can read a file on a network share or through a device path. Copy it to this PC first.";

    public static string DeleteCollectionTitle(string shownAsker, string collection) => $"{shownAsker} wants to delete the collection “{collection}”";

    public static string DeletedCollectionText(string collection) => $"Deleted the collection “{collection}”.";

    public static string DeletedCollectionNotification(string shownAsker, string collection) => $"{shownAsker} deleted the collection “{collection}”.";

    public static string ImportTitle(string shownAsker, string file) => $"{shownAsker} wants to import “{file}”";

    public static string ImportMessage(int count, string collection) => count == 1
        ? $"{count} connector would be copied into “{collection}”, switched off:"
        : $"{count} connectors would be copied into “{collection}”, switched off:";

    public static string ImportNewMessage(int count, string collection) => count == 1
        ? $"{count} connector would be copied into a new collection, “{collection}”, switched off:"
        : $"{count} connectors would be copied into a new collection, “{collection}”, switched off:";

    /// <summary>The import question when a Replace lands over a connector that is on: the copy keeps its
    /// switch, so it stays on.</summary>
    public static string ImportReplacingMessage(int count, string collection) => count == 1
        ? $"{count} connector would be copied into “{collection}”, replacing one that is switched on, so it stays on:"
        : $"{count} connectors would be copied into “{collection}”, switched off, but any that replaces one switched on stays on:";

    /// <summary>The file an import or subscription reads, between the app's quotes so the dialog sets it apart;
    /// <paramref name="path"/> comes through <see cref="ControlText.EscapedPath"/>.</summary>
    public static string FromFooter(string path) => $"From “{path}”";

    public static string ImportedText(int count, string collection) => count == 1
        ? $"Copied {count} connector into “{collection}”, switched off."
        : $"Copied {count} connectors into “{collection}”, switched off.";

    /// <summary>The import's answer when a Replace landed over a connector that was on: the copy kept its
    /// switch, as the question said it would.</summary>
    public static string ImportedReplacingText(int count, string collection) => count == 1
        ? $"Copied {count} connector into “{collection}”; it replaced one that was switched on, so it stays on."
        : $"Copied {count} connectors into “{collection}”, switched off, but any that replaced one switched on stays on.";

    public static string ImportedNotification(string shownAsker, int count, string collection) => count == 1
        ? $"{shownAsker} copied {count} connector into “{collection}”."
        : $"{shownAsker} copied {count} connectors into “{collection}”.";

    public static string NothingToImportError(string file) => $"Nothing in “{file}” would come across.";

    public static string NotAFileError(string file) => $"“{file}” isn’t a file.";

    public static string DocumentTooLargeError(string file) => $"“{file}” is over 1 MB, too large for a collection file.";

    public static string SubscribeTitle(string shownAsker, string file) => $"{shownAsker} wants to subscribe to “{file}”";

    public static string SubscribeMessage(string collection, int count) => count == 1
        ? $"A new collection, “{collection}”, would follow this file, read-only. Its {count} connector arrives switched off:"
        : $"A new collection, “{collection}”, would follow this file, read-only. Its {count} connectors arrive switched off:";

    public static string SubscribedText(string collection, string file) => $"Subscribed: “{collection}” follows {file}, read-only.";

    public static string ArrivedOffText(int count) =>
        count == 1 ? $"{count} connector arrived switched off." : $"{count} connectors arrived switched off.";

    public static string SubscribedNotification(string shownAsker, string file, string collection) => $"{shownAsker} subscribed to “{file}” as “{collection}”.";

    public static string ApplyUpdateTitle(string shownAsker, string collection) => $"{shownAsker} wants to update “{collection}”";

    public static string AppliedUpdateText(string collection) => $"Applied the update to “{collection}”.";

    public static string AppliedUpdateNotification(string shownAsker, string collection) => $"{shownAsker} applied the update to “{collection}”.";

    public static string NoUpdateError(string collection) => $"“{collection}” has no update waiting.";

    public static string RestoreTitle(string shownAsker, string id) => $"{shownAsker} wants to restore “{id}”";

    public static string RestoreIntoText(string collection) => $"Its connectors go back into “{collection}”:";

    public static string RestoreActivatingText(string collection) => $"Its connectors go back into “{collection}”, which becomes the active collection:";

    public static string RestoredText(string id) => $"Restored “{id}”.";

    public static string RestoredNotification(string shownAsker, string id) => $"{shownAsker} restored “{id}”.";

    public static string NoBackupError(string id) => $"There’s no backup named “{id}”.";

    public static string RestartTitle(string shownAsker) => $"{shownAsker} wants to restart Claude Desktop";

    /// <summary>A connector as a question lists it: its name in quotes, through <see cref="ControlText.QuotedList"/>
    /// — so no name can close its quotes, break its line, or pass for a line of what a connector
    /// runs, which are indented under it — then what becomes of it, when that is said.</summary>
    internal static string ConnectorHeading(string name, string? detail = null)
    {
        var quoted = ControlText.QuotedList([name]);
        return detail is null ? quoted : Detail(quoted, ControlText.Escaped(detail));
    }

    /// <summary>What a line under a connector's heading starts with.</summary>
    internal const string Indent = "  ";

    /// <summary>Lines under a connector's heading, indented so they read as its own.</summary>
    internal static IEnumerable<string> Indented(IEnumerable<string> lines) => lines.Select(line => Indent + line);

    /// <summary>A document's connectors as the import and subscribe questions list them, in the Import
    /// sheet's order and words: each by name with what it would run, as the dialog shows a config
    /// (<c>${COLLECTION_DIR}</c> as the document writes it, the footer naming the folder it stands for);
    /// a name the collection already holds with what happens to it; one this platform cannot carry
    /// with why.</summary>
    internal static List<string> DocumentLines(RenderedCollection rendered, Func<string, bool> present, ImportChoice choice)
    {
        var lines = new List<string>();
        foreach (var name in rendered.Connectors.Keys.Union(rendered.Excluded.Keys).Order(StringComparer.Ordinal))
        {
            if (rendered.Excluded.TryGetValue(name, out var reason))
            {
                lines.Add(ConnectorHeading(name, ImportModel.SkippedBadge(reason)));
                continue;
            }
            if (!rendered.Connectors.TryGetValue(name, out var connector))
            {
                continue;
            }
            var runs = Indented(ControlMasking.CodeLines(connector.Config, ControlMasking.Exposure.Dialog)).ToList();
            if (!present(name))
            {
                lines.Add(ConnectorHeading(name));
                lines.AddRange(runs);
                continue;
            }
            switch (choice)
            {
                case ImportChoice.Replace:
                    lines.Add(ConnectorHeading(name, ImportModel.ReplaceTitle));
                    lines.AddRange(runs);
                    break;
                case ImportChoice.KeepBoth:
                    lines.Add(ConnectorHeading(name, ImportModel.KeepBothTitle));
                    lines.AddRange(runs);
                    break;
                default:
                    lines.Add(ConnectorHeading(name, ImportModel.PresentBadge));
                    break;
            }
        }
        return lines;
    }

    /// <summary>The collection's own Delete: the window's question, refused first for what the window
    /// refuses. A subscribed collection's file is never touched, nor a published one's — the window
    /// asks whether that one goes too, and nothing here asks twice, so it stays, as Keep leaves it.</summary>
    private ControlStep DeleteCollection(ControlArguments arguments, string shownAsker)
    {
        var collection = arguments.String("collection") ?? "";
        var shown = ControlText.EscapedName(collection);
        if (state.Store.Collections.GetValueOrDefault(collection) is not { } held)
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(shown));
        }
        var synced = state.IsSynced(collection);
        // AppState's own rule, asked before the question rather than after it.
        if (!synced && state.LocalCollectionNames.Count <= 1)
        {
            return new ControlStep.Refused(AppState.LastLocalCollectionError);
        }
        var active = collection == state.ActiveCollection;
        var count = held.Mcps.Count;
        // The window's Delete confirmation, sentence for sentence and in its order.
        var sentences = new List<string> { CollectionsModel.DeleteConnectorsSentence(count) };
        if (count > 0)
        {
            sentences.Add(CollectionsModel.DeleteCopiesSentence);
        }
        if (active && state.ActiveAfterDeleting(collection) is { } next)
        {
            sentences.Add(CollectionsModel.DeleteNextActiveSentence(ControlText.EscapedName(next)));
        }
        if (synced)
        {
            sentences.Add(CollectionsModel.DeleteSourceSentence);
        }
        if (state.CollectionsCache.Published.ContainsKey(collection) && state.IsPublished(collection))
        {
            sentences.Add(PublishedFileStaysText);
        }
        if (count > 0)
        {
            sentences.Add(CollectionsModel.DeleteCheckedInformative);
        }
        var request = new ConfirmationRequest(DeleteCollectionTitle(shownAsker, shown), string.Join(" ", sentences), [],
            active ? TakesEffectFooter : null);
        return new ControlStep.Ready(request, () =>
        {
            var mark = state.AppliedServers;
            if (state.DeleteCollection(collection) is { } error)
            {
                return Refused(error, [collection]);
            }
            return Done([DeletedCollectionText(shown)],
                [("collection", JsonValue.String(collection)), ("activeCollection", JsonValue.String(state.ActiveCollection))],
                // Only deleting the collection Claude runs reaches its config; deleting another must
                // not report a retry an earlier write left pending.
                active ? null : collection, mark, RestartToApplyText,
                notification: DeletedCollectionNotification(shownAsker, shown));
        });
    }

    /// <summary>The full path a document's <c>path</c> names, as AppState resolves it, or why there is
    /// none. Platform idiom: <see cref="Path.GetFullPath(string)"/> throws for a path it cannot take,
    /// where the Mac mirror's URL takes any text and the read that follows refuses it.</summary>
    private static (string? Full, string? Refusal) DocumentPath(string path)
    {
        try
        {
            return (Path.GetFullPath(path), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, AppState.SourceUnreadableError(Path.GetFileName(path), ex.Message));
        }
    }

    /// <summary>Why <paramref name="caller"/> may not read the document at <paramref name="full"/>, a full path,
    /// by its form alone, or null. Windows-forced, as the <c>cmd /c</c> launcher's rule is: the relay is
    /// refused any path that starts with <c>\\</c> or <c>\??\</c> — a UNC or WebDAV path, or another device
    /// form — since opening one has Windows sign in to that host as the user, with nothing asked, while the
    /// command-line tool keeps its team shares; and a <c>\\.\</c> path, Windows' way to a device or a pipe,
    /// or a <c>\\?\</c> or <c>\??\</c> path to anything but a drive or a share, is refused to everyone, since
    /// the rule cannot tell where it leads. The Mac mirror's paths have no such form. Pure, so it is tested
    /// on any platform.</summary>
    internal static string? PathRefusal(string full, ControlCaller caller)
    {
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal) || IsDeviceNamespace(full))
        {
            // A path, not a name: its backslashes are its own, never doubled, but its curly quotes cannot
            // close the app's quotes around it.
            return NotAFileError(ControlText.EscapedPath(full));
        }
        return caller is ControlCaller.Relay && (full.StartsWith(@"\\", StringComparison.Ordinal) || HasNtPrefix(full))
            ? NetworkPathError : null;
    }

    /// <summary>Whether <paramref name="full"/> starts with <c>\??\</c>, the NT spelling of <c>\\?\</c>: .NET
    /// leaves it as it is and Win32 opens it as the same extended path.</summary>
    private static bool HasNtPrefix(string full) => full.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="full"/> is a <c>\\?\</c> or <c>\??\</c> path to anything but a drive
    /// (<c>\\?\C:\</c>) or a share (<c>\\?\UNC\</c>): a volume by its GUID, <c>GLOBALROOT</c>, a pipe or a
    /// device. The rule cannot tell where these lead — a volume path can be a real file — so they are
    /// refused to every caller, the command line included.</summary>
    private static bool IsDeviceNamespace(string full)
    {
        if (!full.StartsWith(@"\\?\", StringComparison.Ordinal) && !HasNtPrefix(full))
        {
            return false;
        }
        var rest = full.AsSpan(4);
        var drive = rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\';
        return !drive && !rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why the document at <paramref name="full"/> is not read, or null to read it, found before anything
    /// opens it, so no path can stall the app or reach what is not a file: it must be a regular file, or a link
    /// to one — not a folder, a device, or a link to one — of at most <see cref="McpSession.MaxMessageBytes"/>,
    /// the most a message carries. A path with nothing there is left to the read, which says so in the
    /// system's words.</summary>
    private static string? DocumentFileRefusal(string full)
    {
        // Platform idiom: FileInfo, which knows a folder from a file and no more, where the Mac mirror's
        // stat(2) reads every file type; a device or a pipe here is a \\.\ path, or a \\?\ or \??\ one to
        // neither a drive nor a share, which PathRefusal refuses by its form.
        var file = ControlText.EscapedName(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)));
        try
        {
            // A folder, or a link Windows marks as one, is refused before any link is followed.
            if (Directory.Exists(full))
            {
                return NotAFileError(file);
            }
            FileSystemInfo info = new FileInfo(full);
            if (info.LinkTarget is not null)
            {
                // A link is read as what it finally names. Following it opens what it names, so a local
                // link to a UNC path would reach that host here; making one takes write access to this
                // disk already.
                info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
            }
            if (Directory.Exists(info.FullName) || (info.Exists && info.Attributes.HasFlag(FileAttributes.Device)))
            {
                return NotAFileError(file);
            }
            return info is FileInfo { Exists: true } found && found.Length > McpSession.MaxMessageBytes ? DocumentTooLargeError(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A link that loops, or a path that cannot be looked at, is the read's to report.
            return null;
        }
    }

    private ControlStep ImportDocument(ControlArguments arguments, ControlCaller caller)
    {
        var path = arguments.String("path") ?? "";
        // An empty path names the working folder on the Mac and is no path at all here: refused alike
        // on both.
        if (path.Length == 0)
        {
            return new ControlStep.Refused(MissingArgumentError("path"));
        }
        // One place or the other, as the command line's --into | --new.
        if (arguments.String("into") is not null && arguments.String("newCollection") is not null)
        {
            return new ControlStep.Refused(IntoOrNewCollectionError);
        }
        var (full, unusable) = DocumentPath(path);
        if (full is null)
        {
            return new ControlStep.Refused(Shown(unusable ?? EditorModel.NotValidJson, Path.GetFileName(path)));
        }
        if ((PathRefusal(full, caller) ?? DocumentFileRefusal(full)) is { } refusal)
        {
            return new ControlStep.Refused(refusal);
        }
        var file = ControlText.EscapedName(Path.GetFileName(full));
        var (document, bytes, failure) = AppState.ReadDocument(full);
        if (document is null || bytes is null)
        {
            return new ControlStep.Refused(Shown(failure ?? EditorModel.NotValidJson, Path.GetFileName(full)));
        }
        var newCollection = arguments.String("newCollection") is { } requested ? MasterStore.CollectionName(requested) : null;
        string target;
        if (newCollection is not null)
        {
            // The store's own refusal for a name it cannot take, found on a copy so nothing is made.
            if (state.Store.Clone().AddCollection(newCollection) is { } error)
            {
                return new ControlStep.Refused(ControlText.Escaped(error, [newCollection]));
            }
            target = newCollection;
        }
        else
        {
            // The Import sheet's own target: the active collection while it is local, else the first
            // local one.
            var locals = state.LocalCollectionNames;
            target = arguments.String("into")
                ?? (locals.Contains(state.ActiveCollection) ? state.ActiveCollection : locals.FirstOrDefault() ?? state.ActiveCollection);
            if (!state.Store.Collections.ContainsKey(target))
            {
                return new ControlStep.Refused(MasterStore.NoCollectionError(ControlText.EscapedName(target)));
            }
            if (state.IsSynced(target))
            {
                return new ControlStep.Refused(AppState.TargetMustBeLocalError);
            }
        }
        var shownTarget = ControlText.EscapedName(target);
        var clash = arguments.String("onClash") switch
        {
            "replace" => ImportChoice.Replace,
            "keep-both" => ImportChoice.KeepBoth,
            _ => ImportChoice.Skip,
        };
        var rendered = document.Render();
        // A new collection holds nothing yet, whatever an existing one of that name might.
        IReadOnlyDictionary<string, McpEntry> held = newCollection is null && state.Store.Collections.TryGetValue(target, out var existing)
            ? existing.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        var choices = new Dictionary<string, ImportChoice>(StringComparer.Ordinal);
        foreach (var name in rendered.Connectors.Keys)
        {
            choices[name] = held.ContainsKey(name) ? clash : ImportChoice.Add;
        }
        var count = choices.Values.Count(choice => choice != ImportChoice.Skip);
        if (count == 0)
        {
            return new ControlStep.Refused(NothingToImportError(file));
        }
        var shownAsker = caller.ShownAsker;
        // A Replace keeps the switch of what it replaces, as the Import sheet's does: over a connector
        // that is on, the copy stays on and, in the collection Claude runs, changes what Claude runs.
        var replacesOn = choices.Any(choice => choice.Value == ImportChoice.Replace && held.TryGetValue(choice.Key, out var entry) && entry.Enabled);
        var from = FromFooter(ControlText.EscapedPath(full));
        var message = newCollection is not null ? ImportNewMessage(count, shownTarget)
            : replacesOn ? ImportReplacingMessage(count, shownTarget) : ImportMessage(count, shownTarget);
        var request = new ConfirmationRequest(
            ImportTitle(shownAsker, file),
            message,
            DocumentLines(rendered, held.ContainsKey, clash),
            replacesOn && target == state.ActiveCollection ? TakesEffectFooter + " " + from : from);
        // What the question cannot show, the document's masked values among them, the bytes stand for.
        return new ControlStep.Ready(request, Fingerprint: ContentHash.Sha256(bytes), Perform: () =>
        {
            var mark = state.AppliedServers;
            if (newCollection is not null && state.AddEmptyCollection(newCollection) is { } created)
            {
                return Refused(created, [newCollection]);
            }
            if (state.ImportCopies(document, full, target, choices) is { } error)
            {
                return new ControlOutcome(ControlResult.Refusal(Shown(error, Path.GetFileName(full), [target])), null);
            }
            var answer = replacesOn ? ImportedReplacingText(count, shownTarget) : ImportedText(count, shownTarget);
            return Done([answer], [("collection", JsonValue.String(target)), ("count", JsonValue.Int(count))],
                target, mark, RestartToApplyText,
                notification: ImportedNotification(shownAsker, count, shownTarget));
        });
    }

    private ControlStep SubscribeDocument(ControlArguments arguments, ControlCaller caller)
    {
        var path = arguments.String("path") ?? "";
        // As import_document refuses it.
        if (path.Length == 0)
        {
            return new ControlStep.Refused(MissingArgumentError("path"));
        }
        var (full, unusable) = DocumentPath(path);
        if (full is null)
        {
            return new ControlStep.Refused(Shown(unusable ?? EditorModel.NotValidJson, Path.GetFileName(path)));
        }
        // As import_document checks it.
        if ((PathRefusal(full, caller) ?? DocumentFileRefusal(full)) is { } refusal)
        {
            return new ControlStep.Refused(refusal);
        }
        // Platform idiom: as import_document reads it.
        var file = ControlText.EscapedName(Path.GetFileName(full));
        var (document, bytes, failure) = AppState.ReadDocument(full);
        if (document is null || bytes is null)
        {
            return new ControlStep.Refused(Shown(failure ?? EditorModel.NotValidJson, Path.GetFileName(full)));
        }
        // AppState's own rules, asked here as well, so the refusal comes before the question.
        if (document.Origin is { } origin && state.CollectionsFile.Collections.Values.Any(entry => entry.Publish?.Origin == origin))
        {
            return new ControlStep.Refused(AppState.OwnCollectionError);
        }
        var requested = MasterStore.CollectionName(arguments.String("name") ?? "");
        var name = MasterStore.CollectionName(requested.Length == 0 ? document.Name : requested);
        if (state.Store.Clone().AddCollection(name) is { } error)
        {
            return new ControlStep.Refused(ControlText.Escaped(error, [name]));
        }
        var shownName = ControlText.EscapedName(name);
        var rendered = document.Render();
        var count = rendered.Connectors.Count;
        var shownAsker = caller.ShownAsker;
        var request = new ConfirmationRequest(
            SubscribeTitle(shownAsker, file),
            SubscribeMessage(shownName, count),
            DocumentLines(rendered, _ => false, ImportChoice.Add),
            FromFooter(ControlText.EscapedPath(full)));
        // As import_document's: the bytes stand for what the question cannot show.
        return new ControlStep.Ready(request, Fingerprint: ContentHash.Sha256(bytes), Perform: () =>
        {
            var mark = state.AppliedServers;
            if (state.Subscribe(document, bytes, full, arguments.String("name")) is { } refusal)
            {
                return new ControlOutcome(ControlResult.Refusal(Shown(refusal, Path.GetFileName(full), [name])), null);
            }
            return Done([SubscribedText(shownName, file), ArrivedOffText(count)],
                [("collection", JsonValue.String(name)), ("count", JsonValue.Int(count))],
                name, mark, RestartToApplyText,
                notification: SubscribedNotification(shownAsker, file, shownName));
        });
    }

    private ControlStep ApplyUpdate(ControlArguments arguments, string shownAsker)
    {
        var collection = arguments.String("collection") ?? "";
        var shown = ControlText.EscapedName(collection);
        if (state.Store.Collections.GetValueOrDefault(collection) is not { } held)
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(shown));
        }
        if (!state.IsSynced(collection))
        {
            return new ControlStep.Refused(NotSubscribedError(shown));
        }
        if (state.SourceErrors.TryGetValue(collection, out var problem))
        {
            // The problem names the document's file.
            var file = state.SourceBinding(collection)?.Path is { } source ? Path.GetFileName(source) : null;
            return new ControlStep.Refused(file is null ? ControlText.Escaped(problem) : Shown(problem, file));
        }
        if (state.PendingUpdates.GetValueOrDefault(collection) is not { } diff)
        {
            return new ControlStep.Refused(NoUpdateError(shown));
        }
        // Connector by connector, as the Review sheet shows an update: every entry is a command Claude
        // will run. A document that moves under the question then asks a different one, so an Allow
        // cannot land what nobody read.
        var incoming = state.PendingDocument(collection)?.Connectors ?? new Dictionary<string, RenderedConnector>(StringComparer.Ordinal);
        var code = new List<string>();
        foreach (var name in diff.Added)
        {
            code.Add(ConnectorHeading(name, ReviewModel.AddedLabel));
            if (incoming.GetValueOrDefault(name) is { } after)
            {
                code.AddRange(Indented(ControlMasking.CodeLines(after.Config, ControlMasking.Exposure.Dialog)));
            }
        }
        foreach (var name in diff.Removed)
        {
            code.Add(ConnectorHeading(name, ReviewModel.DeletedLabel));
        }
        foreach (var name in diff.Changed)
        {
            code.Add(ConnectorHeading(name, ReviewModel.ChangedLabel));
            if (held.Mcps.GetValueOrDefault(name) is { } before && incoming.GetValueOrDefault(name) is { } after)
            {
                code.AddRange(Indented(ControlMasking.ChangeLines(before.Config, after.Config, ControlMasking.Exposure.Dialog)));
            }
        }
        var active = collection == state.ActiveCollection;
        var request = new ConfirmationRequest(ApplyUpdateTitle(shownAsker, shown),
            PendingUpdateText(shown, ChangesText(diff)),
            code, active ? TakesEffectFooter : null);
        // The document's bytes, as the Review sheet's Apply checks them: what the question cannot show,
        // its masked values among them, must not have moved either.
        return new ControlStep.Ready(request, Fingerprint: state.PendingSourceHash(collection), Perform: () =>
        {
            var mark = state.AppliedServers;
            if (state.ApplyPendingUpdate(collection) is { } error)
            {
                return Refused(error, [collection]);
            }
            return Done([AppliedUpdateText(shown)], [("collection", JsonValue.String(collection))],
                collection, mark, RestartToApplyText,
                notification: AppliedUpdateNotification(shownAsker, shown));
        });
    }

    /// <summary>A refused restore in AppState's own sentences, the collection each names through
    /// <see cref="ControlText.EscapedName"/>: Claude names collections.</summary>
    internal static string RestoreRefusalText(Exception refusal) => refusal switch
    {
        RestoreCollectionGoneException gone => AppState.RestoreCollectionGoneError(ControlText.EscapedName(gone.Collection)),
        RestoreSubscribedException subscribed => AppState.RestoreSubscribedError(ControlText.EscapedName(subscribed.Collection)),
        // RestoreRefusal returns only the two; Swift's switch over its enum is exhaustive.
        _ => ControlText.Escaped(refusal.Message),
    };

    /// <summary>The Restore sheet's restore, by an id <c>list_backups</c> gave, refused first for what the
    /// sheet refuses; the question says which connectors come back, and into which collection.</summary>
    private ControlStep RestoreBackup(ControlArguments arguments, string shownAsker)
    {
        var id = arguments.String("id") ?? "";
        var shownId = ControlText.EscapedName(id);
        List<string> paths;
        try
        {
            paths = BackupPaths();
        }
        catch (Exception error)
        {
            return new ControlStep.Refused(ControlText.Escaped(AppState.Friendly(error)));
        }
        // Only an id the list gives: never a path, so nothing outside the backups folder is read.
        // Compared ordinally, as the Mac mirror compares scalar by scalar.
        if (paths.FirstOrDefault(path => string.Equals(Path.GetFileName(path), id, StringComparison.Ordinal)) is not { } backup)
        {
            return new ControlStep.Refused(NoBackupError(shownId));
        }
        // The backup's record, read once: the refusal, the question and the restore all follow it.
        var recorded = BackupCollections.CollectionOf(backup, state.Service.Paths.BackupsDir);
        if (state.RestoreRefusalForRecorded(recorded) is { } refusal)
        {
            return new ControlStep.Refused(RestoreRefusalText(refusal));
        }
        // A backup that is not a config file is refused now rather than after the question, in the
        // reader's own words, as the Restore sheet shows a failed restore's. Its bytes stand for what the
        // question, which lists only names, cannot show.
        // Read once: the question, the fingerprint and the restore all come from these bytes.
        IReadOnlyDictionary<string, JsonValue> servers;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(backup);
            servers = ClaudeConfigIO.McpServers(bytes);
            ClaudeConfigIO.RequireUniqueKeys(bytes);
        }
        catch (Exception ex) when (ex is ClaudeConfigException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new ControlStep.Refused(ControlText.Escaped(ex.Message, [id]));
        }
        var fingerprint = ContentHash.Sha256(bytes);
        // AppState's rule: back into the collection the backup was taken from, else the active one.
        var target = recorded ?? state.ActiveCollection;
        var shownTarget = ControlText.EscapedName(target);
        var into = target == state.ActiveCollection ? RestoreIntoText(shownTarget) : RestoreActivatingText(shownTarget);
        // Connector Control's own entry is the app's, not a connector the backup brings back.
        var names = servers.Keys.Where(name => name != ClaudeIntegration.EntryName).Order(StringComparer.Ordinal);
        var request = new ConfirmationRequest(RestoreTitle(shownAsker, shownId), RestoreModel.Caption + " " + into,
            names.Select(name => ConnectorHeading(name)).ToList(), TakesEffectFooter);
        return new ControlStep.Ready(request, Fingerprint: fingerprint, Perform: () =>
        {
            var mark = state.AppliedServers;
            try
            {
                state.RestoreClaudeConfig(backup, bytes, recorded);
            }
            catch (Exception ex) when (ex is RestoreCollectionGoneException or RestoreSubscribedException)
            {
                return new ControlOutcome(ControlResult.Refusal(RestoreRefusalText(ex)), null);
            }
            catch (Exception ex) when (ex is ClaudeConfigException or IOException or UnauthorizedAccessException or JsonException)
            {
                // Platform idiom: the Restore sheet's own set, as it catches them; anything else is left to
                // the write queue, which ends the call. The Mac mirror's catch takes every other error,
                // as its sheet's does.
                // A restore that failed is the banner's too, as the Restore sheet's is, in the error's
                // own words.
                state.LastError = ex.Message;
                return Refused(ex.Message, [id]);
            }
            return Done([RestoredText(shownId)], [("id", JsonValue.String(id)), ("activeCollection", JsonValue.String(state.ActiveCollection))],
                null, mark, RestartToApplyText,
                notification: RestoredNotification(shownAsker, shownId));
        });
    }

    /// <summary>Always asked about when Claude calls it, whatever the confirmation setting; never asked about
    /// from the command line, where the typed command is the confirmation, as the notification's
    /// Restart button is. Either way it is <c>PerformRestartClaudeAsync</c>, not the in-app button's
    /// <c>RestartClaudeAsync</c>: the gate has asked already, or need not, and the app's own "confirm
    /// before restarting" question would ask a second time.</summary>
    private ControlStep RestartClaude(string shownAsker)
    {
        var request = new ConfirmationRequest(
            shownAsker == ControlCaller.ClaudeName ? RestartItselfTitle : RestartTitle(shownAsker),
            RestartMessage, [], null);
        return new ControlStep.Ready(request, () =>
        {
            // Claude hears the answer first: the restart quits it, and the relay with it. One still due
            // when the app quits never happens. Fire-and-forget, as the toast's Restart button calls it:
            // the restart reports through AppState's own banner, as the Mac's reports through its
            // completion.
            host.Delay(TimeSpan.FromSeconds(RestartDelay), () =>
            {
                if (!state.IsDisposed)
                {
                    _ = state.PerformRestartClaudeAsync();
                }
            });
            return new ControlOutcome(new ControlResult(JsonValue.Object(("restarting", JsonValue.Bool(true))), RestartingText, false), null);
        });
    }
}
