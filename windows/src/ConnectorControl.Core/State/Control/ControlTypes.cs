namespace ConnectorControl.Core.State;

// The values the control layer passes around: who is calling, how a tool is guarded, what it
// declares, what it answers, and what it asks the user.

/// <summary>
/// Who is calling: the command-line tool, trusted as the user, or the relay Claude Desktop runs —
/// which is also what anything but a well-formed command-line hello is taken for, so the gate
/// guards it. C# has no enum payloads, so the two callers are records.
///
/// Mirror: Sources/ConnectorControlState/Control/ControlTypes.swift
/// </summary>
public abstract record ControlCaller
{
    public sealed record Cli : ControlCaller;

    public sealed record Relay(string ClientName) : ControlCaller;

    public const string ClaudeName = "Claude";
    /// <summary>How Claude Desktop names itself in <c>initialize</c>, for chat and for its agent mode.</summary>
    public static readonly IReadOnlyList<string> ClaudeClientPrefixes = ["claude", "local-agent-mode-"];
    public const string UnknownClientName = "Another app";

    /// <summary>
    /// Who the client is, not what a sentence shows (that is <see cref="ShownAsker"/>): "Claude" for Claude
    /// Desktop, whose clientInfo name starts with one of <see cref="ClaudeClientPrefixes"/>, in any case; any other
    /// client by its own name; one that gave none by a name that claims nothing. The command-line tool
    /// goes by the tool's own name.
    /// </summary>
    public string AskerName
    {
        get
        {
            if (this is not Relay relay)
            {
                return MasterStore.ReservedConnectorName;
            }
            // Claude Desktop starts each connector twice: once for chat ("claude-ai") and once for
            // its agent mode ("local-agent-mode-<server key>"). Both are Claude. Compared literally, by
            // code units, as the Mac mirror compares: canonical equivalence would tell them apart.
            if (ClaudeClientPrefixes.Any(prefix => relay.ClientName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return ClaudeName;
            }
            return relay.ClientName.Length == 0 ? UnknownClientName : relay.ClientName;
        }
    }

    /// <summary>
    /// A client that is not Claude Desktop, by the name it gave itself: it chose that name, so it is
    /// framed as a claim, and nobody takes it for this app's own tool or for a product the app vouches for.
    /// </summary>
    public static string CallingItselfText(string name) => $"An app calling itself “{name}”";

    /// <summary>
    /// Who is asking, ready for a sentence: "Claude" for Claude Desktop; any other client as
    /// <see cref="CallingItselfText"/> frames its own name, escaped, so the frame's quotes are the only raw
    /// ones, and cut as a dialog cuts any name (<see cref="TextRun.CutName"/>), so a name of any length
    /// leaves room for the rest of a notification as well as of a question; one that gave no name as
    /// <see cref="UnknownClientName"/>. Every question and notification that names the asker takes this. The
    /// command-line tool is never asked or announced, and keeps the tool's name.
    /// </summary>
    public string ShownAsker =>
        this is Relay relay && AskerName != ClaudeName && relay.ClientName.Length > 0
            ? CallingItselfText(TextRun.CutName(ControlText.EscapedName(relay.ClientName), ConfirmationDialog.NameLimit))
            : AskerName;
}

/// <summary>
/// How a tool is guarded when Claude calls it: reads and direct changes run; a gated change asks
/// first while "Confirm changes Claude makes" is on; a restart always asks.
/// </summary>
public enum ControlGateClass { Read, Direct, Gated, AlwaysConfirm }

/// <summary>
/// The gate's rule, apart from any one tool. The command-line tool is the user and is never gated.
/// Claude is refused everything while the integration is off — including between turning it off and
/// Claude restarting without the entry.
/// </summary>
public static class ControlGate
{
    /// <summary>C# has no enum payloads, so each decision is a record; records compare by value.</summary>
    public abstract record Decision
    {
        public sealed record Refuse(string Reason) : Decision;

        public sealed record Run : Decision;

        public sealed record Confirm : Decision;

        /// <summary>Runs without asking, and announces the change.</summary>
        public sealed record RunAndNotify : Decision;
    }

    public static Decision Decide(ControlCaller caller, ControlGateClass gate, bool integrationEnabled, bool confirmChanges)
    {
        if (caller is ControlCaller.Cli)
        {
            return new Decision.Run();
        }
        if (!integrationEnabled)
        {
            return new Decision.Refuse(ControlTools.IntegrationOffError);
        }
        return gate switch
        {
            ControlGateClass.Read or ControlGateClass.Direct => new Decision.Run(),
            ControlGateClass.Gated => confirmChanges ? new Decision.Confirm() : new Decision.RunAndNotify(),
            _ => new Decision.Confirm(),
        };
    }
}

/// <summary>
/// The four MCP hints, every one stated, so Claude Desktop sorts the tool into its read-only or
/// write group however its defaults change.
/// </summary>
public sealed record ControlToolAnnotations(bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld)
{
    public JsonValue McpAnnotations => JsonValue.Object(
        ("readOnlyHint", JsonValue.Bool(ReadOnly)),
        ("destructiveHint", JsonValue.Bool(Destructive)),
        ("idempotentHint", JsonValue.Bool(Idempotent)),
        ("openWorldHint", JsonValue.Bool(OpenWorld)));
}

/// <summary>
/// One tool, as Claude sees it in <c>tools/list</c> and as <c>help</c> lists its command.
/// <c>Title</c> is what Claude's connector settings show for the tool; without it Claude shows the
/// bare name.
/// </summary>
public sealed record ControlToolDefinition(
    string Name, string Title, string Description, JsonValue InputSchema, ControlToolAnnotations Annotations,
    ControlGateClass Gate, string CliUsage, string CliGroup)
{
    /// <summary>The command's line in <c>help</c>: the description's first sentence, without its full stop.</summary>
    public string Summary
    {
        get
        {
            var end = Description.IndexOf(". ", StringComparison.Ordinal);
            if (end >= 0)
            {
                return Description[..end];
            }
            return Description.EndsWith('.') ? Description[..^1] : Description;
        }
    }

    /// <summary>The <c>tools/list</c> entry.</summary>
    public JsonValue McpDescriptor => JsonValue.Object(
        ("name", JsonValue.String(Name)),
        ("title", JsonValue.String(Title)),
        ("description", JsonValue.String(Description)),
        ("inputSchema", InputSchema),
        ("annotations", Annotations.McpAnnotations));
}

/// <summary>
/// What a tool answers: the structure <c>--json</c> prints and Claude receives as
/// <c>structuredContent</c>, the sentence a person reads, and whether it was refused.
/// </summary>
public sealed record ControlResult(JsonValue Structured, string Text, bool IsError)
{
    /// <summary>A refusal: the app's own sentence, as text and as <c>{"error": …}</c>.</summary>
    public static ControlResult Refusal(string text) => new(JsonValue.Object(("error", JsonValue.String(text))), text, true);

    /// <summary>The <c>tools/call</c> result.</summary>
    public JsonValue McpResult => JsonValue.Object(
        ("content", JsonValue.Array([JsonValue.Object(("type", JsonValue.String("text")), ("text", JsonValue.String(Text)))])),
        ("structuredContent", Structured),
        ("isError", JsonValue.Bool(IsError)));
}

/// <summary>
/// What a confirmation shows: who is asking for what, what would run, and a closing line.
/// <c>Code</c> holds the monospace lines — what would run, masked; empty for none. Equality
/// compares them line by line: a record would compare the list by reference.
/// </summary>
public sealed record ConfirmationRequest(string Title, string Message, IReadOnlyList<string> Code, string? Footer)
{
    // Platform idiom: text is compared ordinally, as the Mac mirror compares it scalar by scalar; Swift's
    // == would hold canonically equivalent text equal, and take a question that changed only in how a
    // character is composed for the one the user answered.
    public bool Equals(ConfirmationRequest? other) =>
        other is not null && Title == other.Title && Message == other.Message
        && Code.SequenceEqual(other.Code, StringComparer.Ordinal) && Footer == other.Footer;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Title, StringComparer.Ordinal);
        hash.Add(Message, StringComparer.Ordinal);
        foreach (var line in Code)
        {
            hash.Add(line, StringComparer.Ordinal);
        }
        hash.Add(Footer, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// The confirmation dialog: the app shows it with Don't Allow as the default answer and calls
/// <c>completion</c> with true only for Allow. The write queue relies on this contract:
/// <list type="bullet">
/// <item><c>completion</c> is called exactly once on every path, the dialog's window closed without an
/// answer included (as Don't Allow);</item>
/// <item><c>Confirm</c> may return before the user answers or only after (a modal dialog): the call's wait
/// has started before it is asked, and its progress and early answer run in the dialog's run loop;</item>
/// <item>a <c>completion</c> never called holds the write queue for good: every later relay write only ever
/// answers "queued", and every later command-line write waits for good;</item>
/// <item>C# only: it never throws once its dialog is showing. It may throw before showing anything,
/// which refuses the call with <c>NoQuestionError</c>, never with the exception's text; after that, a failure calls <c>completion(false)</c>.</item>
/// </list>
/// </summary>
public interface IControlConfirmer
{
    void Confirm(ConfirmationRequest request, Action<bool> completion);
}

/// <summary>Answers every question Don't Allow, at once: the confirmer of a host that can show no dialog —
/// the test hosts; the apps ask in their own (<c>DialogConfirmer</c>) — so a change that must be asked about is
/// refused, never made unasked. It keeps the confirmer's contract: one completion, every time. It never
/// throws.</summary>
public sealed class DecliningConfirmer : IControlConfirmer
{
    public void Confirm(ConfirmationRequest request, Action<bool> completion) => completion(false);
}

/// <summary>The settings <c>get_settings</c> lists and <c>set_setting</c> takes, by the names the command line uses.</summary>
public static class ControlSettingKey
{
    public const string LaunchAtLogin = "launch-at-login";
    public const string ConfirmRestart = "confirm-restart";
    public const string ConfirmQuit = "confirm-quit";
    public const string NotifyExternalChanges = "notify-external-changes";
    public const string AutomaticUpdates = "automatic-updates";
    public const string BackupsToKeep = "backups-to-keep";
    public const string ClaudeIntegration = "claude-integration";
    public const string ConfirmClaudeChanges = "confirm-claude-changes";
    public static readonly IReadOnlyList<string> All = [LaunchAtLogin, ConfirmRestart, ConfirmQuit, NotifyExternalChanges,
                                                        AutomaticUpdates, BackupsToKeep, ClaudeIntegration, ConfirmClaudeChanges];

    /// <summary>Only the user changes these, in Settings or with the command-line tool: Claude may not
    /// widen what it is allowed to do, silence the notice of changes synced in from another
    /// machine, or stop the app updating.</summary>
    public static bool IsUserOnly(string key) =>
        key == ClaudeIntegration || key == ConfirmClaudeChanges || key == NotifyExternalChanges || key == AutomaticUpdates;
}
