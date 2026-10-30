import ConnectorControlCore
import Foundation

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTypes.cs
///
/// The values the control layer passes around: who is calling, how a tool is guarded, what it
/// declares, what it answers, and what it asks the user.

/// Who is calling: the command-line tool, trusted as the user, or the relay Claude Desktop runs —
/// which is also what anything but a well-formed command-line hello is taken for, so the gate
/// guards it. The Windows mirror has no enum payloads, so its two callers are records.
public enum ControlCaller: Equatable, Sendable {
    case cli
    case relay(clientName: String)

    public static let claudeName = "Claude"
    /// How Claude Desktop names itself in `initialize`, for chat and for its agent mode.
    public static let claudeClientPrefixes = ["claude", "local-agent-mode-"]
    public static let unknownClientName = "Another app"

    /// Who the client is, not what a sentence shows (that is `shownAsker`): "Claude" for Claude
    /// Desktop, whose clientInfo name starts with one of `claudeClientPrefixes`, in any case; any other
    /// client by its own name; one that gave none by a name that claims nothing. The command-line tool
    /// goes by the tool's own name.
    public var askerName: String {
        switch self {
        case .cli:
            return MasterStore.reservedConnectorName
        case .relay(let clientName):
            // Claude Desktop starts each connector twice: once for chat ("claude-ai") and once for
            // its agent mode ("local-agent-mode-<server key>"). Both are Claude. Compared literally, by
            // code units, as the Windows mirror compares: canonical equivalence would tell them apart.
            if ControlCaller.claudeClientPrefixes.contains(where: {
                clientName.range(of: $0, options: [.caseInsensitive, .anchored, .literal]) != nil
            }) {
                return ControlCaller.claudeName
            }
            return clientName.isEmpty ? ControlCaller.unknownClientName : clientName
        }
    }

    /// A client that is not Claude Desktop, by the name it gave itself: it chose that name, so it is
    /// framed as a claim, and nobody takes it for this app's own tool or for a product the app vouches for.
    public static func callingItselfText(_ name: String) -> String { "An app calling itself “\(name)”" }

    /// Who is asking, ready for a sentence: "Claude" for Claude Desktop; any other client as
    /// `callingItselfText(_:)` frames its own name, escaped, so the frame's quotes are the only raw
    /// ones, and cut as a dialog cuts any name (`TextRun.cutName(_:limit:)`), so a name of any length
    /// leaves room for the rest of a notification as well as of a question; one that gave no name as
    /// `unknownClientName`. Every question and notification that names the asker takes this. The
    /// command-line tool is never asked or announced, and keeps the tool's name.
    public var shownAsker: String {
        guard case .relay(let clientName) = self, askerName != ControlCaller.claudeName, !clientName.isEmpty else { return askerName }
        return ControlCaller.callingItselfText(TextRun.cutName(ControlText.escapedName(clientName), limit: ConfirmationDialog.nameLimit))
    }
}

/// How a tool is guarded when Claude calls it: reads and direct changes run; a gated change asks
/// first while "Confirm changes Claude makes" is on; a restart always asks.
public enum ControlGateClass: Equatable, Sendable {
    case read, direct, gated, alwaysConfirm
}

/// The gate's rule, apart from any one tool. The command-line tool is the user and is never gated.
/// Claude is refused everything while the integration is off — including between turning it off and
/// Claude restarting without the entry.
@MainActor
public enum ControlGate {
    /// The Windows mirror has no enum payloads, so each decision there is a record; records compare by value.
    public enum Decision: Equatable, Sendable {
        case refuse(String)
        case run
        case confirm
        /// Runs without asking, and announces the change.
        case runAndNotify
    }

    public static func decide(caller: ControlCaller, gate: ControlGateClass,
                              integrationEnabled: Bool, confirmChanges: Bool) -> Decision {
        guard caller != .cli else { return .run }
        guard integrationEnabled else { return .refuse(ControlTools.integrationOffError) }
        switch gate {
        case .read, .direct: return .run
        case .gated: return confirmChanges ? .confirm : .runAndNotify
        case .alwaysConfirm: return .confirm
        }
    }
}

/// The four MCP hints, every one stated, so Claude Desktop sorts the tool into its read-only or
/// write group however its defaults change.
public struct ControlToolAnnotations: Equatable, Sendable {
    public let readOnly: Bool
    public let destructive: Bool
    public let idempotent: Bool
    public let openWorld: Bool

    public init(readOnly: Bool, destructive: Bool, idempotent: Bool, openWorld: Bool) {
        self.readOnly = readOnly
        self.destructive = destructive
        self.idempotent = idempotent
        self.openWorld = openWorld
    }

    public var mcpAnnotations: JSONValue {
        .object([
            "readOnlyHint": .bool(readOnly),
            "destructiveHint": .bool(destructive),
            "idempotentHint": .bool(idempotent),
            "openWorldHint": .bool(openWorld),
        ])
    }
}

/// One tool, as Claude sees it in `tools/list` and as `help` lists its command.
public struct ControlToolDefinition: Equatable, Sendable {
    public let name: String
    /// What Claude's connector settings show for the tool; without it Claude shows the bare name.
    public let title: String
    public let description: String
    public let inputSchema: JSONValue
    public let annotations: ControlToolAnnotations
    public let gate: ControlGateClass
    public let cliUsage: String
    public let cliGroup: String

    public init(name: String, title: String, description: String, inputSchema: JSONValue, annotations: ControlToolAnnotations,
                gate: ControlGateClass, cliUsage: String, cliGroup: String) {
        self.name = name
        self.title = title
        self.description = description
        self.inputSchema = inputSchema
        self.annotations = annotations
        self.gate = gate
        self.cliUsage = cliUsage
        self.cliGroup = cliGroup
    }

    /// The command's line in `help`: the description's first sentence, without its full stop.
    public var summary: String {
        if let end = description.range(of: ". ") { return String(description[..<end.lowerBound]) }
        return description.hasSuffix(".") ? String(description.dropLast()) : description
    }

    /// The `tools/list` entry.
    public var mcpDescriptor: JSONValue {
        .object([
            "name": .string(name),
            "title": .string(title),
            "description": .string(description),
            "inputSchema": inputSchema,
            "annotations": annotations.mcpAnnotations,
        ])
    }
}

/// What a tool answers: the structure `--json` prints and Claude receives as `structuredContent`,
/// the sentence a person reads, and whether it was refused.
public struct ControlResult: Equatable, Sendable {
    public let structured: JSONValue
    public let text: String
    public let isError: Bool

    public init(structured: JSONValue, text: String, isError: Bool) {
        self.structured = structured
        self.text = text
        self.isError = isError
    }

    /// A refusal: the app's own sentence, as text and as `{"error": …}`.
    public static func refusal(_ text: String) -> ControlResult {
        ControlResult(structured: .object(["error": .string(text)]), text: text, isError: true)
    }

    /// The `tools/call` result.
    public var mcpResult: JSONValue {
        .object([
            "content": .array([.object(["type": .string("text"), "text": .string(text)])]),
            "structuredContent": structured,
            "isError": .bool(isError),
        ])
    }
}

/// What a confirmation shows: who is asking for what, what would run, and a closing line. Equality
/// compares `code` line by line, as an array does; the Windows mirror says so itself, since a C#
/// record would compare the list by reference.
public struct ConfirmationRequest: Equatable, Sendable {
    public let title: String
    public let message: String
    /// Monospace lines: what would run, masked; empty for none.
    public let code: [String]
    public let footer: String?

    public init(title: String, message: String, code: [String], footer: String?) {
        self.title = title
        self.message = message
        self.code = code
        self.footer = footer
    }

    // Platform idiom: text is compared scalar by scalar, as the Windows mirror compares it ordinally;
    // Swift's == holds canonically equivalent text equal, and would take a question that changed only
    // in how a character is composed for the one the user answered.
    public static func == (one: ConfirmationRequest, other: ConfirmationRequest) -> Bool {
        let footers: Bool
        switch (one.footer, other.footer) {
        case (nil, nil): footers = true
        case (let a?, let b?): footers = same(a, b)
        default: footers = false
        }
        return same(one.title, other.title) && same(one.message, other.message)
            && one.code.count == other.code.count && zip(one.code, other.code).allSatisfy { same($0, $1) }
            && footers
    }

    /// The same text, scalar for scalar. A request is compared off the main actor, so this cannot
    /// lean on ControlMasking's own.
    private static func same(_ one: String, _ other: String) -> Bool {
        one.unicodeScalars.elementsEqual(other.unicodeScalars)
    }
}

/// The confirmation dialog: the app shows it with Don't Allow as the default answer and calls
/// `completion` with true only for Allow. The write queue relies on this contract:
/// - `completion` is called exactly once on every path, the dialog's window closed without an
///   answer included (as Don't Allow);
/// - `confirm` may return before the user answers or only after (a modal dialog): the call's wait
///   has started before it is asked, and its progress and early answer run in the dialog's run loop;
/// - a `completion` never called holds the write queue for good: every later relay write only ever
///   answers "queued", and every later command-line write waits for good.
/// The Windows mirror adds that its confirmer never throws once its dialog is showing; one that throws
/// before showing anything is refused with `noQuestionError`, never with the exception's text.
@MainActor
public protocol ControlConfirmer: AnyObject {
    func confirm(_ request: ConfirmationRequest, completion: @escaping (Bool) -> Void)
}

/// Answers every question Don't Allow, at once: the confirmer of a host that can show no dialog —
/// the test hosts; the apps ask in their own (`DialogConfirmer`) — so a change that must be asked about is
/// refused, never made unasked. It keeps the confirmer's contract: one completion, every time.
@MainActor
public final class DecliningConfirmer: ControlConfirmer {
    public init() {}

    public func confirm(_ request: ConfirmationRequest, completion: @escaping (Bool) -> Void) {
        completion(false)
    }
}

/// The settings `get_settings` lists and `set_setting` takes, by the names the command line uses.
public enum ControlSettingKey {
    public static let launchAtLogin = "launch-at-login"
    public static let confirmRestart = "confirm-restart"
    public static let confirmQuit = "confirm-quit"
    public static let notifyExternalChanges = "notify-external-changes"
    public static let automaticUpdates = "automatic-updates"
    public static let backupsToKeep = "backups-to-keep"
    public static let claudeIntegration = "claude-integration"
    public static let confirmClaudeChanges = "confirm-claude-changes"
    public static let all = [launchAtLogin, confirmRestart, confirmQuit, notifyExternalChanges,
                             automaticUpdates, backupsToKeep, claudeIntegration, confirmClaudeChanges]

    /// Only the user changes these, in Settings or with the command-line tool: Claude may not
    /// widen what it is allowed to do, silence the notice of changes synced in from another
    /// machine, or stop the app updating.
    public static func isUserOnly(_ key: String) -> Bool {
        key == claudeIntegration || key == confirmClaudeChanges || key == notifyExternalChanges || key == automaticUpdates
    }
}
