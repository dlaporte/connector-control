namespace ConnectorControl.Core.State;

/// <summary>
/// The control layer's tools: one list Claude sees through MCP and the command-line tool runs as
/// commands, each a thin handler on AppState's own operations with the rules the windows apply. It
/// knows nothing about transport — <c>McpSession</c> and the command-line client reach it through
/// <c>ControlToolHost</c> — and the gate is decided here, once, for every caller.
///
/// Reads answer at once. Writes run one at a time, in the order they arrive. One that asks the user
/// holds the line until the user answers, however long that takes, while its caller hears progress
/// every <see cref="ProgressInterval"/> and, at <see cref="ConfirmationWait"/>, that the answer is
/// still awaited. The command line, which Claude's time limit does not bind, is never answered early: it
/// hears progress until its own write has run.
///
/// One per app: every connection's <c>ControlToolHost</c> shares the one instance. Claude Desktop opens
/// two relay connections, one for chat and one for its agent mode, beside any command-line one, and
/// the write queue that runs changes one at a time is this instance's.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlTools.swift
/// </summary>
public sealed partial class ControlTools
{
    public const string IntegrationOffError = "Claude integration is turned off in Connector Control.";
    public const string DeclinedError = "You declined this change in Connector Control.";
    public const string WaitingText = "Waiting for your answer in Connector Control.";
    public const string QueuedText = "Waiting for another change in Connector Control to finish.";
    public const string ChangedWhileAskingError = "This changed while Connector Control was asking, so nothing was done. Ask again.";
    public const string NoQuestionError = "Connector Control couldn’t ask about this change, so nothing was done.";
    public const string ArgumentsNotObjectError = "The arguments must be a JSON object.";
    public const string OnWord = "on";
    public const string OffWord = "off";
    public const string TextKind = "text";
    public const string BooleanKind = "true or false";
    public const string IntegerKind = "a whole number";
    public const string ListKind = "a list of names";
    public const string ObjectKind = "a JSON object";
    public const string HelpUsage = "Usage: connector-control <command> [options]";
    public const string RelayUsage = "mcp";
    public const string RelaySummary = "The relay Claude Desktop runs; not for direct use";
    public const string HelpOptions = "Options: --json  --version  --help";
    /// <summary>The width of the usage field in <c>help</c>: two spaces of indent, then the usage padded to this, then the
    /// summary, so every summary starts in column 38. A usage too long to leave two spaces goes alone on its
    /// line and its summary follows on the next, indented to the same column.</summary>
    public const int HelpColumn = 35;
    /// <summary>Seconds between progress reports while a change waits.</summary>
    public const double ProgressInterval = 5;
    /// <summary>How long, in seconds, a call waits before it answers <see cref="WaitingText"/>: under
    /// Claude Desktop's tool-call timeout, which is what this constant is tuned to.</summary>
    public const double ConfirmationWait = 50;
    /// <summary>Seconds between answering a restart and starting it, so the answer reaches Claude before it
    /// quits.</summary>
    public const double RestartDelay = 1;

    public static string UnknownToolError(string name) => $"There’s no tool named “{name}”.";

    /// <summary>A tool the running app lacks, asked for by a newer command-line tool or relay: an update
    /// replaced the app on disk while the old one still runs.</summary>
    public static string NeedsNewerAppError(string version) => $"This needs Connector Control {version}; restart the app.";

    public static string MissingArgumentError(string name) => $"“{name}” is required.";

    public static string WrongTypeError(string name, string kind) => $"“{name}” must be {kind}.";

    public static string NotOneOfError(string name, string options) => $"“{name}” must be one of {options}.";

    public static string Detail(string first, string second) => $"{first} · {second}";

    /// <summary>The early answer of a call whose confirmation is open.</summary>
    internal static ControlResult WaitingResult =>
        new(JsonValue.Object(("status", JsonValue.String("waiting"))), WaitingText, false);

    /// <summary>The early answer of a call still waiting for its turn, behind another change: it has asked
    /// nobody anything yet.</summary>
    internal static ControlResult QueuedResult =>
        new(JsonValue.Object(("status", JsonValue.String("queued"))), QueuedText, false);

    internal readonly AppState state;
    internal readonly SettingsModel settings;
    internal readonly IControlConfirmer confirmer;
    internal readonly AppHost host;
    /// <summary>Writes waiting for their turn, oldest first.</summary>
    private readonly List<ToolCall> writeQueue = [];
    /// <summary>A write is running, or waiting for the user's answer.</summary>
    private bool writing;
    /// <summary>Stands in for the write tools' own preparation when set, so tests drive the write queue with
    /// writes they control. Null in the app.</summary>
    internal Func<string, ControlArguments, ControlCaller, ControlStep>? PrepareOverride { get; set; }

    public ControlTools(AppState state, SettingsModel settings, IControlConfirmer confirmer, AppHost host)
    {
        this.state = state;
        this.settings = settings;
        this.confirmer = confirmer;
        this.host = host;
    }

    /// <summary>The running app's version alone, without the build number the Mac shows beside it:
    /// <c>get_status</c> says it, the MCP session introduces itself with it, and a hello's version is
    /// compared with it.</summary>
    public string Version => ControlVersion.Semantic(settings.Version) ?? settings.Version;

    /// <summary>Runs one tool for <paramref name="caller"/>. <paramref name="progress"/> reports how many
    /// seconds a change has waited; <paramref name="completion"/> is called once, or never for a write dropped
    /// because its connection closed first. <paramref name="isAbandoned"/> says the caller's connection has
    /// closed: a write still waiting for its turn then, whose caller was never told it is waiting, is dropped
    /// unrun.</summary>
    public void Call(string name, JsonValue arguments, ControlCaller caller, Action<double> progress, Action<ControlResult> completion,
                     Func<bool>? isAbandoned = null)
    {
        var definition = Catalog.FirstOrDefault(tool => tool.Name == name);
        if (definition is null)
        {
            completion(ControlResult.Refusal(UnknownToolError(name)));
            return;
        }
        if (arguments.Kind != JsonKind.Object)
        {
            completion(ControlResult.Refusal(ArgumentsNotObjectError));
            return;
        }
        var values = arguments.ObjectProperties;
        if (ArgumentError(values, definition.InputSchema) is { } error)
        {
            completion(ControlResult.Refusal(error));
            return;
        }
        if (Decision(definition, caller) is ControlGate.Decision.Refuse refuse)
        {
            completion(ControlResult.Refusal(refuse.Reason));
            return;
        }
        var call = new ToolCall(definition, new ControlArguments(values), caller, progress, completion, isAbandoned ?? (() => false));
        if (definition.Gate == ControlGateClass.Read)
        {
            Read(definition.Name, call.Arguments, caller, result => Deliver(call, result));
            return;
        }
        writeQueue.Add(call);
        if (writing)
        {
            StartWaiting(call);
        }
        else
        {
            StartNextWrite();
        }
    }

    /// <summary><c>help</c>: every tool's command in its group, with the first sentence of its description. Static,
    /// since it reads only the catalog: the command-line tool prints it without an app.</summary>
    public static string HelpText()
    {
        var lines = new List<string> { HelpUsage };
        foreach (var group in HelpGroups)
        {
            var rows = Catalog.Where(tool => tool.CliGroup == group).Select(tool => (Usage: tool.CliUsage, tool.Summary)).ToList();
            if (group == ClaudeGroup)
            {
                rows.Add((RelayUsage, RelaySummary));
            }
            if (rows.Count == 0)
            {
                continue;
            }
            lines.Add(group);
            foreach (var (usage, summary) in rows)
            {
                // Widths count runes (`delete <name>…` holds U+2026, one wide); the Mac mirror counts
                // Unicode scalars, so both agree where a .NET string's UTF-16 would not.
                if (ControlText.Width(usage) + 2 > HelpColumn)
                {
                    lines.Add("  " + usage);
                    lines.Add(new string(' ', 2 + HelpColumn) + summary);
                }
                else
                {
                    lines.Add("  " + ControlText.Pad(usage, HelpColumn) + summary);
                }
            }
        }
        lines.Add(HelpOptions);
        return string.Join("\n", lines);
    }

    private ControlGate.Decision Decision(ControlToolDefinition definition, ControlCaller caller) =>
        ControlGate.Decide(caller, definition.Gate, state.Settings.ClaudeIntegrationEnabled, state.Settings.ConfirmClaudeChanges);

    // MARK: the write queue

    private void StartNextWrite()
    {
        if (writing || writeQueue.Count == 0)
        {
            return;
        }
        var call = writeQueue[0];
        writeQueue.RemoveAt(0);
        // A write whose connection closed before its turn, and whose caller was never told it is
        // waiting, is dropped unrun: nobody is left to hear of it, and a command line stopped with
        // Control-C has taken it back. One already told it is queued runs, as it was told.
        // Outside the catches below on purpose: the one supplier of `IsAbandoned`, the host's
        // `IsOpen`, cannot throw, and the Mac's closure cannot by its type.
        if (!call.Delivered && call.IsAbandoned())
        {
            call.Finished = true;
            call.Delivered = true;
            StartNextWrite();
            return;
        }
        writing = true;
        // Caught here, where the Mac has nothing to catch: a C# handler, the confirmer or the caller's
        // completion can throw, and a write that threw must still end and let the next start. Swift's
        // closures here do not throw, so nothing there can leave the line held.
        try
        {
            // Decided again: the settings may have changed while the call waited for its turn.
            switch (Decision(call.Definition, call.Caller))
            {
                case ControlGate.Decision.Refuse refuse:
                    Finish(call, ControlResult.Refusal(refuse.Reason));
                    break;
                case ControlGate.Decision.Run:
                {
                    var outcome = Run(call);
                    Announce(call, outcome, unasked: false);
                    Finish(call, outcome.Result);
                    break;
                }
                case ControlGate.Decision.RunAndNotify:
                {
                    var outcome = Run(call);
                    Announce(call, outcome, unasked: true);
                    Finish(call, outcome.Result);
                    break;
                }
                case ControlGate.Decision.Confirm:
                    switch (Prepare(call.Definition.Name, call.Arguments, call.Caller))
                    {
                        case ControlStep.Refused refused:
                            Finish(call, ControlResult.Refusal(refused.Reason));
                            break;
                        case ControlStep.Ready ready:
                            // Every tool that asks builds its question; one that did not is refused rather than
                            // run unasked.
                            if (ready.Request is not { } request)
                            {
                                Finish(call, ControlResult.Refusal(NoQuestionError));
                                return;
                            }
                            // The wait starts before the question is put: the app's dialog is modal, so
                            // Confirm may return only once the user has answered, and the progress and
                            // early answer run inside its run loop meanwhile. One answered at once leaves a
                            // tick that finds the call answered and does nothing.
                            call.Asking = true;
                            StartWaiting(call);
                            confirmer.Confirm(request, allowed => Answered(call, request, ready.Fingerprint, allowed));
                            break;
                    }
                    break;
            }
        }
        catch (Exception error)
        {
            Failed(call, error);
        }
    }

    /// <summary>Prepares and performs a write that does not ask.</summary>
    private ControlOutcome Run(ToolCall call) => Prepare(call.Definition.Name, call.Arguments, call.Caller) switch
    {
        ControlStep.Refused refused => new ControlOutcome(ControlResult.Refusal(refused.Reason), null),
        ControlStep.Ready ready => ready.Perform(),
        // Platform idiom: a C# switch expression must cover every value, and a record hierarchy can
        // grow; a step is refused or ready, as the Mac's enum says without this arm.
        _ => throw new InvalidOperationException("A write is refused or ready."),
    };

    /// <summary>The user's answer. What they allowed is what they were shown: the change is prepared again
    /// against the state as it is now and must ask the very same question, and stand for the very same
    /// thing where the question cannot show it (its fingerprint), or nothing happens. An answer to a call
    /// that has already ended — one whose dialog threw — changes nothing.</summary>
    private void Answered(ToolCall call, ConfirmationRequest request, string? fingerprint, bool allowed)
    {
        if (call.Answered || call.Finished)
        {
            return;
        }
        call.Answered = true;
        // Caught here as where a write starts; Swift's closures here do not throw.
        try
        {
            var outcome = new ControlOutcome(ControlResult.Refusal(DeclinedError), null);
            if (allowed)
            {
                // Decided again: the integration may have been turned off while the dialog was open, and
                // an Allow then must not reach past it.
                if (Decision(call.Definition, call.Caller) is ControlGate.Decision.Refuse refuse)
                {
                    outcome = new ControlOutcome(ControlResult.Refusal(refuse.Reason), null);
                }
                else
                {
                    switch (Prepare(call.Definition.Name, call.Arguments, call.Caller))
                    {
                        case ControlStep.Refused refused:
                            outcome = new ControlOutcome(ControlResult.Refusal(refused.Reason), null);
                            break;
                        case ControlStep.Ready ready:
                            // A fingerprint is hex digits, which == compares exactly on both platforms.
                            outcome = Equals(ready.Request, request) && ready.Fingerprint == fingerprint
                                ? ready.Perform()
                                : new ControlOutcome(ControlResult.Refusal(ChangedWhileAskingError), null);
                            break;
                    }
                }
            }
            Announce(call, outcome, unasked: false);
            Finish(call, outcome.Result);
        }
        catch (Exception error)
        {
            Failed(call, error);
        }
    }

    /// <summary>Announces a change that landed when nobody else will hear of it: always for one made without
    /// asking, for a direct change that moved what Claude runs (<c>Announces</c>), and for any other once
    /// its caller has already been told it is still waiting, or its connection has closed and it will
    /// never hear the answer, so a change Claude asked for never lands unannounced. The command line's
    /// own changes are the user's, and never announced. The tool's own notification says it, or else its
    /// answer; it is posted once, however many of these hold.</summary>
    private void Announce(ToolCall call, ControlOutcome outcome, bool unasked)
    {
        if (outcome.Result.IsError || call.Caller is ControlCaller.Cli || !(unasked || outcome.Announces || call.Delivered || call.IsAbandoned()))
        {
            return;
        }
        // Caught here, where the Mac has nothing to catch: a C# notifier can throw, and Swift's does
        // not. The change has applied whatever the notifier does, so its answer stays a success —
        // a refusal would have Claude ask again for a change that already landed.
        try
        {
            state.NotifyClaudeChange(outcome.Notification ?? outcome.Result.Text);
        }
        catch (Exception)
        {
            // The announcement is lost; the change and its answer stand.
        }
    }

    /// <summary>Answers the call, unless it already has its answer, and lets the next write start. A call
    /// ends once.</summary>
    private void Finish(ToolCall call, ControlResult result)
    {
        if (call.Finished)
        {
            return;
        }
        call.Finished = true;
        // The next write starts even when the answer throws; the Mac's answer cannot throw.
        try
        {
            Deliver(call, result);
        }
        finally
        {
            writing = false;
            StartNextWrite();
        }
    }

    /// <summary>A write that threw ends as one refused for the reason, unless it has already ended. C# only:
    /// nothing in the Mac's queue can throw.</summary>
    private void Failed(ToolCall call, Exception error) => Finish(call, ControlResult.Refusal(AppState.Friendly(error)));

    private void Deliver(ToolCall call, ControlResult result)
    {
        if (call.Delivered)
        {
            return;
        }
        call.Delivered = true;
        call.Completion(result);
    }

    // MARK: waiting

    /// <summary>Progress every <see cref="ProgressInterval"/> while a call waits — for its turn, or for the
    /// user's answer — and, at <see cref="ConfirmationWait"/>, the early answer that says so. The call
    /// carries on: its turn still comes, and the user's answer still counts. The command line is never answered
    /// early, and a call whose connection has closed hears nothing more and gets no early answer.</summary>
    private void StartWaiting(ToolCall call)
    {
        if (call.Waiting || call.Delivered)
        {
            return;
        }
        call.Waiting = true;
        ScheduleTick(call);
    }

    private void ScheduleTick(ToolCall call)
    {
        host.Delay(TimeSpan.FromSeconds(ProgressInterval), () =>
        {
            // A call whose connection has closed hears nothing more — no progress, no early answer — so
            // one still waiting its turn stays untold, and is dropped when its turn comes.
            if (call.Delivered || call.IsAbandoned())
            {
                return;
            }
            call.Ticks++;
            var waited = call.Ticks * ProgressInterval;
            call.Progress(waited);
            if (waited >= ConfirmationWait && call.Caller is not ControlCaller.Cli)
            {
                // Only a call whose own dialog is open is waiting for an answer; any other is waiting
                // for its turn. The command line is never answered early: it is not bound by Claude's
                // time limit, and the person who ran it waits for the change itself.
                Deliver(call, call.Asking ? WaitingResult : QueuedResult);
            }
            else
            {
                ScheduleTick(call);
            }
        });
    }

    /// <summary>One call on its way through. Named <c>ToolCall</c> where the Mac names it <c>Call</c>: C#
    /// cannot name a nested type after a method of its class.</summary>
    private sealed class ToolCall(ControlToolDefinition definition, ControlArguments arguments, ControlCaller caller,
                              Action<double> progress, Action<ControlResult> completion, Func<bool> isAbandoned)
    {
        public ControlToolDefinition Definition { get; } = definition;
        public ControlArguments Arguments { get; } = arguments;
        public ControlCaller Caller { get; } = caller;
        public Action<double> Progress { get; } = progress;
        public Action<ControlResult> Completion { get; } = completion;
        public Func<bool> IsAbandoned { get; } = isAbandoned;
        /// <summary>The caller has its answer — perhaps the early one that says the user's is awaited.</summary>
        public bool Delivered { get; set; }
        /// <summary>Its confirmation has been put to the user.</summary>
        public bool Asking { get; set; }
        /// <summary>The user has answered the confirmation.</summary>
        public bool Answered { get; set; }
        /// <summary>It has ended, and the next write may start.</summary>
        public bool Finished { get; set; }
        public bool Waiting { get; set; }
        public int Ticks { get; set; }
    }

    // MARK: arguments

    /// <summary>The arguments checked against the tool's schema before anything runs: every required one
    /// present, every one present of its declared type and, where it has one, among its <c>enum</c>.
    /// Arguments the schema does not name are ignored. Null when they pass.</summary>
    internal static string? ArgumentError(IReadOnlyDictionary<string, JsonValue> arguments, JsonValue schema)
    {
        if (schema.Kind != JsonKind.Object)
        {
            return null;
        }
        if (schema["required"] is { Kind: JsonKind.Array } required)
        {
            foreach (var name in required.ArrayItems.Where(item => item.Kind == JsonKind.String).Select(item => item.StringValue))
            {
                if (!arguments.ContainsKey(name))
                {
                    return MissingArgumentError(name);
                }
            }
        }
        if (schema["properties"] is not { Kind: JsonKind.Object } properties)
        {
            return null;
        }
        foreach (var name in arguments.Keys.Order(StringComparer.Ordinal))
        {
            var value = arguments[name];
            if (properties[name] is not { Kind: JsonKind.Object } property
                || property["type"] is not { Kind: JsonKind.String } typeValue)
            {
                continue;
            }
            var type = typeValue.StringValue;
            if (!Matches(value, type))
            {
                return WrongTypeError(name, KindWord(type));
            }
            if (property["enum"] is { Kind: JsonKind.Array } options && !options.ArrayItems.Contains(value))
            {
                var words = options.ArrayItems.Where(option => option.Kind == JsonKind.String).Select(option => option.StringValue);
                return NotOneOfError(name, string.Join(", ", words));
            }
        }
        return null;
    }

    internal static bool Matches(JsonValue value, string type) => (type, value.Kind) switch
    {
        ("string", JsonKind.String) or ("boolean", JsonKind.Bool) or ("integer", JsonKind.Int) or ("object", JsonKind.Object) => true,
        ("array", JsonKind.Array) => value.ArrayItems.All(item => item.Kind == JsonKind.String),
        _ => false,
    };

    internal static string KindWord(string type) => type switch
    {
        "boolean" => BooleanKind,
        "integer" => IntegerKind,
        "array" => ListKind,
        "object" => ObjectKind,
        _ => TextKind,
    };
}

/// <summary>
/// A write, prepared against the state as it is now: refused with the reason, or ready — with the
/// question to ask when the tool asks, and the change itself. C# has no enum payloads, so the two
/// steps are records.
/// <para>
/// <c>Fingerprint</c>, never shown, stands for what the change would land where the question cannot
/// show it — a document's or a backup's bytes, whose secrets the dialog masks — so an Allow lands only
/// while that is unchanged too. It is declared last, where C# puts an optional parameter; the Mac
/// mirror declares it before the change.
/// </para>
/// </summary>
internal abstract record ControlStep
{
    public sealed record Refused(string Reason) : ControlStep;

    public sealed record Ready(ConfirmationRequest? Request, Func<ControlOutcome> Perform, string? Fingerprint = null) : ControlStep;
}

/// <summary>What a performed write answers, and what a notification says about it when nobody was asked.
/// <c>Announces</c>: a direct change that moved what Claude runs, which the user hears of although
/// nobody was asked and its caller has its answer.</summary>
internal sealed record ControlOutcome(ControlResult Result, string? Notification, bool Announces = false);

/// <summary>A call's arguments by name. The schema check has already run, so a present argument has the
/// type its schema declares.</summary>
internal sealed record ControlArguments(IReadOnlyDictionary<string, JsonValue> Values)
{
    public string? String(string name) => Values.TryGetValue(name, out var value) && value.Kind == JsonKind.String ? value.StringValue : null;

    public bool? Bool(string name) => Values.TryGetValue(name, out var value) && value.Kind == JsonKind.Bool ? value.BoolValue : null;

    public IReadOnlyList<string>? Strings(string name) =>
        Values.TryGetValue(name, out var value) && value.Kind == JsonKind.Array
            ? value.ArrayItems.Where(item => item.Kind == JsonKind.String).Select(item => item.StringValue).ToList()
            : null;

    public JsonValue? Object(string name) => Values.TryGetValue(name, out var value) && value.Kind == JsonKind.Object ? value : null;
}
