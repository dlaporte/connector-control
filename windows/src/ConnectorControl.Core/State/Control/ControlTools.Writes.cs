using System.Globalization;

namespace ConnectorControl.Core.State;

/// <summary>
/// The write tools that change nothing a connector runs, apart from switching one on or off: they
/// never ask, and say what they did and what follows from it.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlTools+Writes.swift
/// </summary>
public sealed partial class ControlTools
{
    public const string RestartToUseText = "Restart Claude to use it.";
    public const string RestartToApplyText = "Restart Claude to apply the change.";
    public const string KeepCountError = "backups-to-keep takes a number from 5 to 100.";

    public static string SavedNotAppliedText(string message) => $"Saved, but Claude’s config wasn’t updated: {message}";

    public static string TurnedOnText(string name, string collection) => $"Turned on “{name}” in “{collection}”.";

    public static string TurnedOffText(string name, string collection) => $"Turned off “{name}” in “{collection}”.";

    public static string SwitchedText(string collection) => $"Switched to “{collection}”.";

    public static string CreatedText(string collection) => $"Created “{collection}”.";

    public static string RenamedCollectionText(string old, string @new) => $"Renamed the collection “{old}” to “{@new}”.";

    public static string DuplicatedText(string source, string copy) => $"Copied “{source}” as “{copy}”.";

    public static string DuplicateSubscribedError(string collection) => $"“{collection}” is subscribed; copy it in Connector Control with Make Local Copy.";

    public static string SettingChangedText(string key, string value) => $"{key} is now {value}.";

    public static string IntegrationSetText(string state) => $"Claude integration is {state}.";

    public static string SettingNotForClaudeError(string key) => $"Only you can change {key}, in Connector Control or with the command-line tool.";

    public static string SwitchValueError(string key) => $"{key} takes on or off.";

    /// <summary>Prepares the write <paramref name="name"/> against the state as it is now: every refusal is
    /// found here, before anyone is asked, and the change is performed only by the step's <c>Perform</c>.</summary>
    internal ControlStep Prepare(string name, ControlArguments arguments, ControlCaller caller)
    {
        if (PrepareOverride is { } prepareOverride)
        {
            return prepareOverride(name, arguments, caller);
        }
        switch (name)
        {
            case "set_connector_enabled": return SetEnabled(arguments);
            case "switch_collection": return SwitchCollection(arguments);
            case "create_collection": return CreateCollection(arguments);
            case "rename_collection": return RenameCollection(arguments);
            case "duplicate_collection": return DuplicateCollection(arguments);
            case "refresh_subscription": return RefreshSubscription(arguments);
            case "set_setting": return SetSetting(arguments, caller);
            default: return new ControlStep.Refused(UnknownToolError(name));
        }
    }

    internal static ControlOutcome Refused(string text) => new(ControlResult.Refusal(text), null);

    /// <summary>A write's answer: what happened, then what follows for the collection Claude runs. A write
    /// that changed what Claude's config holds (<c>AppliedServers</c> moved off <paramref name="mark"/>, taken
    /// before the write) while Claude runs an older config says to restart; one that could not write it says
    /// the change was saved and gives the app's own message. <paramref name="collection"/> null is a write that
    /// reaches Claude whatever is active. The notification, for a change nobody was asked about,
    /// carries the same restart sentence.</summary>
    internal ControlOutcome Done(List<string> lines, IReadOnlyList<(string Key, JsonValue Value)> fields, string? collection,
                                 IReadOnlyDictionary<string, JsonValue> mark, string restart, string? notification = null)
    {
        var text = lines.ToList();
        var reachesClaude = collection is null || collection == state.ActiveCollection;
        // Compared by value: the harness clock stands still, so a timestamp could not tell a write from none.
        var applied = !DictionaryEquality.Equal(state.AppliedServers, mark);
        string? then = null;
        if (reachesClaude && state.ApplyRetryNeeded)
        {
            text.Add(SavedNotAppliedText(state.LastError ?? ""));
        }
        else if (applied && state.NeedsClaudeRestart)
        {
            // A one-sentence result takes the restart on the same line, as "Turned on “x” in “y”.
            // Restart Claude to use it." reads; a longer one gives it a line of its own.
            then = restart;
            if (text.Count == 1)
            {
                text[0] += " " + restart;
            }
            else
            {
                text.Add(restart);
            }
        }
        var structured = fields.Concat([
            ("restartRequired", JsonValue.Bool(applied && state.NeedsClaudeRestart)),
            ("applied", JsonValue.Bool(!(reachesClaude && state.ApplyRetryNeeded))),
        ]).ToArray();
        return new ControlOutcome(
            new ControlResult(JsonValue.Object(structured), string.Join("\n", text), false),
            notification is null ? null : then is null ? notification : notification + " " + then);
    }

    private ControlStep SetEnabled(ControlArguments arguments)
    {
        var name = arguments.String("name") ?? "";
        var on = arguments.Bool("enabled") ?? true;
        // The active collection only, as the popover's switches act: it is the one Claude runs.
        var collection = state.ActiveCollection;
        if (!state.Store.Collections.TryGetValue(collection, out var held) || !held.Mcps.ContainsKey(name))
        {
            return new ControlStep.Refused(NoConnectorError(name, collection));
        }
        return new ControlStep.Ready(null, () =>
        {
            var mark = state.AppliedServers;
            state.SetEnabled(name, on);
            return Done([on ? TurnedOnText(name, collection) : TurnedOffText(name, collection)],
                [("collection", JsonValue.String(collection)), ("name", JsonValue.String(name)), ("enabled", JsonValue.Bool(on))],
                collection, mark, on ? RestartToUseText : RestartToApplyText);
        });
    }

    private ControlStep SwitchCollection(ControlArguments arguments)
    {
        var collection = arguments.String("collection") ?? "";
        // AppState ignores an unknown name in silence, as the chip menu can never offer one;
        // a caller that typed it hears why nothing happened.
        if (!state.Store.Collections.ContainsKey(collection))
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(collection));
        }
        return new ControlStep.Ready(null, () =>
        {
            var mark = state.AppliedServers;
            state.SwitchCollection(collection);
            return Done([SwitchedText(collection)], [("activeCollection", JsonValue.String(collection))],
                null, mark, RestartToApplyText);
        });
    }

    private ControlStep CreateCollection(ControlArguments arguments)
    {
        var name = arguments.String("name") ?? "";
        return new ControlStep.Ready(null, () =>
        {
            var mark = state.AppliedServers;
            if (state.AddEmptyCollection(name) is { } error)
            {
                return Refused(error);
            }
            var created = MasterStore.CollectionName(name);
            return Done([CreatedText(created)], [("collection", JsonValue.String(created))],
                created, mark, RestartToApplyText);
        });
    }

    private ControlStep RenameCollection(ControlArguments arguments)
    {
        var collection = arguments.String("collection") ?? "";
        var newName = arguments.String("newName") ?? "";
        return new ControlStep.Ready(null, () =>
        {
            var mark = state.AppliedServers;
            if (state.RenameCollection(collection, newName) is { } error)
            {
                return Refused(error);
            }
            var renamed = MasterStore.CollectionName(newName);
            return Done([RenamedCollectionText(collection, renamed)],
                [("collection", JsonValue.String(renamed)), ("previousName", JsonValue.String(collection))],
                renamed, mark, RestartToApplyText);
        });
    }

    private ControlStep DuplicateCollection(ControlArguments arguments)
    {
        var source = arguments.String("collection") ?? "";
        var newName = arguments.String("newName") ?? "";
        if (!state.Store.Collections.ContainsKey(source))
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(source));
        }
        // AppState passes over a subscribed source in silence, since the window offers Make Local
        // Copy in its place; that is not a tool here, so the caller is told where to find it.
        if (state.IsSynced(source))
        {
            return new ControlStep.Refused(DuplicateSubscribedError(source));
        }
        return new ControlStep.Ready(null, () =>
        {
            var mark = state.AppliedServers;
            if (state.DuplicateCollection(source, newName) is { } error)
            {
                return Refused(error);
            }
            var copy = MasterStore.CollectionName(newName);
            return Done([DuplicatedText(source, copy)], [("collection", JsonValue.String(copy)), ("source", JsonValue.String(source))],
                copy, mark, RestartToApplyText);
        });
    }

    private ControlStep RefreshSubscription(ControlArguments arguments)
    {
        var collection = arguments.String("collection") ?? "";
        if (!state.Store.Collections.ContainsKey(collection))
        {
            return new ControlStep.Refused(MasterStore.NoCollectionError(collection));
        }
        if (!state.IsSynced(collection))
        {
            return new ControlStep.Refused(NotSubscribedError(collection));
        }
        return new ControlStep.Ready(null, () =>
        {
            state.RefreshSource(collection);
            return new ControlOutcome(PendingUpdate(collection), null);
        });
    }

    private ControlStep SetSetting(ControlArguments arguments, ControlCaller caller)
    {
        var key = arguments.String("key") ?? "";
        var raw = (arguments.String("value") ?? "").Trim(' ', '\t');
        if (caller is not ControlCaller.Cli && ControlSettingKey.IsUserOnly(key))
        {
            return new ControlStep.Refused(SettingNotForClaudeError(key));
        }
        if (key == ControlSettingKey.BackupsToKeep)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || count < SettingsModel.MinKeepCount || count > SettingsModel.MaxKeepCount)
            {
                return new ControlStep.Refused(KeepCountError);
            }
            return new ControlStep.Ready(null, () =>
            {
                var mark = state.AppliedServers;
                settings.BackupKeepCount = count;
                return Done([SettingChangedText(key, count.ToString(CultureInfo.InvariantCulture))],
                    [("key", JsonValue.String(key)), ("value", JsonValue.Int(count))],
                    null, mark, RestartToApplyText);
            });
        }
        var lowered = raw.ToLowerInvariant();
        if (lowered is not ("on" or "off" or "true" or "false"))
        {
            return new ControlStep.Refused(SwitchValueError(key));
        }
        var on = lowered is "on" or "true";
        return new ControlStep.Ready(null, () =>
        {
            var mark = state.AppliedServers;
            var word = on ? OnWord : OffWord;
            // The integration's own sentence names it as Settings does; every other key reads back
            // as the command line typed it.
            var lines = new List<string>
            {
                key == ControlSettingKey.ClaudeIntegration ? IntegrationSetText(word) : SettingChangedText(key, word),
            };
            switch (key)
            {
                case ControlSettingKey.LaunchAtLogin:
                    // Windows calls launch at login "launch at startup"; the key is the same on both.
                    // Read fresh first, as the General tab does: the OS may have changed it since.
                    settings.Refresh();
                    settings.LaunchAtStartup = on;
                    if (settings.LaunchAtStartup != on)
                    {
                        return Refused(settings.LoginItemNote ?? "");
                    }
                    if (settings.LoginItemNote is { } note)
                    {
                        lines.Add(note);
                    }
                    break;
                case ControlSettingKey.ConfirmRestart: settings.ConfirmBeforeRestart = on; break;
                case ControlSettingKey.ConfirmQuit: settings.ConfirmBeforeQuit = on; break;
                case ControlSettingKey.NotifyExternalChanges: settings.NotifyExternalChanges = on; break;
                case ControlSettingKey.AutomaticUpdates: settings.AutoUpdate = on; break;
                case ControlSettingKey.ClaudeIntegration:
                    if (state.SetClaudeIntegrationEnabled(on) is { } refusal)
                    {
                        return Refused(refusal);
                    }
                    break;
                case ControlSettingKey.ConfirmClaudeChanges: state.SetConfirmClaudeChanges(on); break;
                default:
                    return Refused(NotOneOfError("key", string.Join(", ", ControlSettingKey.All)));
            }
            return Done(lines, [("key", JsonValue.String(key)), ("value", JsonValue.Bool(on))],
                null, mark, on ? RestartToUseText : RestartToApplyText);
        });
    }
}
