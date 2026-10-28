namespace ConnectorControl.Core.State;

/// <summary>
/// The writes that change what a connector runs. Each is prepared in full before anyone is asked:
/// every refusal, then the question — who is asking, for what, and exactly what would run, with
/// secrets masked — then the change itself, which runs only on Allow (or with confirmations off,
/// or for the command-line tool) and is announced when nobody was asked.
///
/// The question, the answer and the announcement name connectors, collections and the asker as
/// others chose them — Claude names connectors, creates and renames collections without asking, and
/// any client names itself — so every name goes through <see cref="ControlText.EscapedName"/>: none can
/// break a line, reorder or hide text, or close its own quotes.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlTools+Gated.swift
/// </summary>
public sealed partial class ControlTools
{
    public const string TakesEffectFooter = "Takes effect when Claude restarts.";
    public const string ConfigShapeError = "A connector’s config needs a “command” or a “url”.";
    public const string NothingToChangeError = "Give a new config, a new name, or both.";

    public static string AddTitle(string asker, string name) => $"{asker} wants to add “{name}”";

    public static string AddActiveMessage(string collection, string state) => $"It would join “{collection}”, the collection Claude is running, switched {state}, and run:";

    public static string AddMessage(string collection, string state) => $"It would join “{collection}”, switched {state}, and run:";

    public static string ChangeTitle(string asker, string name) => $"{asker} wants to change “{name}”";

    public static string ChangeActiveMessage(string collection) => $"In “{collection}”, the collection Claude is running:";

    public static string ChangeMessage(string collection) => $"In “{collection}”:";

    public static string DeleteTitle(string asker, string names) => $"{asker} wants to delete {names}";

    public static string DeleteActiveMessage(string collection) => $"From “{collection}”, the collection Claude is running. A copy remains in Backups.";

    public static string DeleteMessage(string collection) => $"From “{collection}”. A copy remains in Backups.";

    public static string AddedText(string name, string collection, string state) => $"Added “{name}” to “{collection}”, switched {state}.";

    public static string NeedsValuesText(string names) => $"It needs a value for {names}: fill it in Connector Control.";

    public static string ChangedText(string name, string collection) => $"Changed “{name}” in “{collection}”.";

    public static string RenamedText(string old, string @new) => $"Renamed “{old}” to “{@new}”.";

    public static string DeletedText(string names, string collection) => $"Deleted {names} from “{collection}”.";

    public static string UnchangedError(string name) => $"“{name}” already has that config.";

    public static string AddedNotification(string asker, string name, string collection) => $"{asker} added “{name}” to “{collection}”.";

    public static string ChangedNotification(string asker, string name, string collection) => $"{asker} changed “{name}” in “{collection}”.";

    public static string RenamedNotification(string asker, string old, string @new) => $"{asker} renamed “{old}” to “{@new}”.";

    public static string DeletedNotification(string asker, string names, string collection) => $"{asker} deleted {names} from “{collection}”.";

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

    private ControlStep AddConnector(ControlArguments arguments, string asker)
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
        var shownAsker = ControlText.EscapedName(asker);
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
                return Refused(refusal);
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

    private ControlStep UpdateConnector(ControlArguments arguments, string asker)
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
        var shownAsker = ControlText.EscapedName(asker);
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
                return Refused(refusal);
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

    private ControlStep DeleteConnectors(ControlArguments arguments, string asker)
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
        var shownAsker = ControlText.EscapedName(asker);
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
}
