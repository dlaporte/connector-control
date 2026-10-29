import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTools.cs
///
/// The control layer's tools: one list Claude sees through MCP and the command-line tool runs as
/// commands, each a thin handler on AppState's own operations with the rules the windows apply. It
/// knows nothing about transport — `McpSession` and the command-line client reach it through
/// `ControlToolHost` — and the gate is decided here, once, for every caller.
///
/// Reads answer at once. Writes run one at a time, in the order they arrive. One that asks the user
/// holds the line until the user answers, however long that takes, while its caller hears progress
/// every `progressInterval` and, at `confirmationWait`, that the answer is still awaited. The command
/// line, which Claude's time limit does not bind, is never answered early: it hears progress until
/// its own write has run.
///
/// One per app: every connection's `ControlToolHost` shares the one instance. Claude Desktop opens
/// two relay connections, one for chat and one for its agent mode, beside any command-line one, and
/// the write queue that runs changes one at a time is this instance's.
@MainActor
public final class ControlTools {
    public static let integrationOffError = "Claude integration is turned off in Connector Control."
    public static let declinedError = "You declined this change in Connector Control."
    public static let waitingText = "Waiting for your answer in Connector Control."
    public static let queuedText = "Waiting for another change in Connector Control to finish."
    public static let changedWhileAskingError = "This changed while Connector Control was asking, so nothing was done. Ask again."
    public static let noQuestionError = "Connector Control couldn’t ask about this change, so nothing was done."
    public static let argumentsNotObjectError = "The arguments must be a JSON object."
    public static let onWord = "on"
    public static let offWord = "off"
    public static let textKind = "text"
    public static let booleanKind = "true or false"
    public static let integerKind = "a whole number"
    public static let listKind = "a list of names"
    public static let objectKind = "a JSON object"
    public static let helpUsage = "Usage: connector-control <command> [options]"
    public static let relayUsage = "mcp"
    public static let relaySummary = "The relay Claude Desktop runs; not for direct use"
    public static let helpOptions = "Options: --json  --version  --help"
    /// The width of the usage field in `help`: two spaces of indent, then the usage padded to this, then the
    /// summary, so every summary starts in column 38. A usage too long to leave two spaces goes alone on its
    /// line and its summary follows on the next, indented to the same column.
    public static let helpColumn = 35
    /// Seconds between progress reports while a change waits.
    public static let progressInterval: TimeInterval = 5
    /// How long a call waits before it answers `waitingText`: under Claude Desktop's tool-call
    /// timeout, which is what this constant is tuned to.
    public static let confirmationWait: TimeInterval = 50
    /// Seconds between answering a restart and starting it, so the answer reaches Claude before it
    /// quits.
    public static let restartDelay: TimeInterval = 1

    public static func unknownToolError(_ name: String) -> String { "There’s no tool named “\(name)”." }

    /// A tool the running app lacks, asked for by a newer command-line tool or relay: an update
    /// replaced the app on disk while the old one still runs.
    public static func needsNewerAppError(_ version: String) -> String { "This needs Connector Control \(version); restart the app." }

    public static func missingArgumentError(_ name: String) -> String { "“\(name)” is required." }

    public static func wrongTypeError(_ name: String, _ kind: String) -> String { "“\(name)” must be \(kind)." }

    public static func notOneOfError(_ name: String, _ options: String) -> String { "“\(name)” must be one of \(options)." }

    public static func detail(_ first: String, _ second: String) -> String { "\(first) · \(second)" }

    /// The early answer of a call whose confirmation is open.
    static var waitingResult: ControlResult {
        ControlResult(structured: .object(["status": .string("waiting")]), text: waitingText, isError: false)
    }

    /// The early answer of a call still waiting for its turn, behind another change: it has asked
    /// nobody anything yet.
    static var queuedResult: ControlResult {
        ControlResult(structured: .object(["status": .string("queued")]), text: queuedText, isError: false)
    }

    let state: AppState
    let settings: SettingsModel
    let confirmer: ControlConfirmer
    let host: AppHost
    /// Writes waiting for their turn, oldest first.
    private var writeQueue: [Call] = []
    /// A write is running, or waiting for the user's answer.
    private var writing = false
    /// Stands in for the write tools' own preparation when set, so tests drive the write queue with
    /// writes they control. nil in the app.
    var prepareOverride: ((String, ControlArguments, ControlCaller) -> ControlStep)?

    public init(state: AppState, settings: SettingsModel, confirmer: ControlConfirmer, host: AppHost) {
        self.state = state
        self.settings = settings
        self.confirmer = confirmer
        self.host = host
    }

    /// The running app's version alone, without the build number the Mac shows beside it:
    /// `get_status` says it, the MCP session introduces itself with it, and a hello's version is
    /// compared with it.
    public var version: String { ControlVersion.semantic(settings.version) ?? settings.version }

    /// Runs one tool for `caller`. `progress` reports how many seconds a change has waited;
    /// `completion` is called once, or never for a write dropped because its connection closed first.
    /// `isAbandoned` says the caller's connection has closed: a write still waiting for its turn then,
    /// whose caller was never told it is waiting, is dropped unrun.
    public func call(name: String, arguments: JSONValue, caller: ControlCaller,
                     progress: @escaping (Double) -> Void, completion: @escaping (ControlResult) -> Void,
                     isAbandoned: @escaping () -> Bool = { false }) {
        guard let definition = ControlTools.catalog.first(where: { $0.name == name }) else {
            completion(.refusal(ControlTools.unknownToolError(ControlText.escapedName(name))))
            return
        }
        guard case .object(let values) = arguments else {
            completion(.refusal(ControlTools.argumentsNotObjectError))
            return
        }
        if let error = ControlTools.argumentError(values, definition.inputSchema) {
            completion(.refusal(error))
            return
        }
        if case .refuse(let reason) = decision(definition, caller) {
            completion(.refusal(reason))
            return
        }
        let call = Call(definition: definition, arguments: ControlArguments(values: values), caller: caller,
                        progress: progress, completion: completion, isAbandoned: isAbandoned)
        guard definition.gate != .read else {
            read(definition.name, call.arguments, caller) { [weak self] result in self?.deliver(call, result) }
            return
        }
        writeQueue.append(call)
        if writing { startWaiting(call) } else { startNextWrite() }
    }

    /// `help`: every tool's command in its group, with the first sentence of its description. Static,
    /// since it reads only the catalog: the command-line tool prints it without an app.
    public static func helpText() -> String {
        var lines = [ControlTools.helpUsage]
        for group in ControlTools.helpGroups {
            var rows = ControlTools.catalog.filter { $0.cliGroup == group }.map { ($0.cliUsage, $0.summary) }
            if group == ControlTools.claudeGroup { rows.append((ControlTools.relayUsage, ControlTools.relaySummary)) }
            guard !rows.isEmpty else { continue }
            lines.append(group)
            for (usage, summary) in rows {
                // Widths count Unicode scalars (`delete <name>…` holds U+2026, one wide); the Windows
                // mirror counts runes, so both agree where a .NET string's UTF-16 would not.
                if ControlText.width(usage) + 2 > ControlTools.helpColumn {
                    lines.append("  " + usage)
                    lines.append(String(repeating: " ", count: 2 + ControlTools.helpColumn) + summary)
                } else {
                    lines.append("  " + ControlText.pad(usage, to: ControlTools.helpColumn) + summary)
                }
            }
        }
        lines.append(ControlTools.helpOptions)
        return lines.joined(separator: "\n")
    }

    private func decision(_ definition: ControlToolDefinition, _ caller: ControlCaller) -> ControlGate.Decision {
        ControlGate.decide(caller: caller, gate: definition.gate,
                           integrationEnabled: state.settings.claudeIntegrationEnabled,
                           confirmChanges: state.settings.confirmClaudeChanges)
    }

    // MARK: The write queue

    private func startNextWrite() {
        guard !writing, !writeQueue.isEmpty else { return }
        let call = writeQueue.removeFirst()
        // A write whose connection closed before its turn, and whose caller was never told it is
        // waiting, is dropped unrun: nobody is left to hear of it, and a command line stopped with
        // Control-C has taken it back. One already told it is queued runs, as it was told.
        // The Windows mirror calls `IsAbandoned` outside its catches: its one supplier, the host's
        // `IsOpen`, cannot throw, and neither can this closure by its type.
        if !call.delivered, call.isAbandoned() {
            call.finished = true
            call.delivered = true
            startNextWrite()
            return
        }
        writing = true
        // The Windows mirror catches around what follows: a C# handler, the confirmer or the
        // caller's completion can throw, and a write that threw must still end and let the next
        // start. Swift's closures here do not throw, so nothing can leave the line held.
        // Decided again: the settings may have changed while the call waited for its turn.
        switch decision(call.definition, call.caller) {
        case .refuse(let reason):
            finish(call, .refusal(reason))
        case .run:
            let outcome = run(call)
            announce(call, outcome, unasked: false)
            finish(call, outcome.result)
        case .runAndNotify:
            let outcome = run(call)
            announce(call, outcome, unasked: true)
            finish(call, outcome.result)
        case .confirm:
            switch prepare(call.definition.name, call.arguments, call.caller) {
            case .refused(let reason):
                finish(call, .refusal(reason))
            case .ready(let request, let fingerprint, _):
                // Every tool that asks builds its question; one that did not is refused rather than
                // run unasked.
                guard let request else {
                    finish(call, .refusal(ControlTools.noQuestionError))
                    return
                }
                // The wait starts before the question is put: the app's dialog is modal, so
                // `confirm` may return only once the user has answered, and the progress and early
                // answer run inside its run loop meanwhile. One answered at once leaves a tick that
                // finds the call answered and does nothing.
                call.asking = true
                startWaiting(call)
                confirmer.confirm(request) { [weak self] allowed in self?.answered(call, request, fingerprint, allowed) }
            }
        }
    }

    /// Prepares and performs a write that does not ask.
    private func run(_ call: Call) -> ControlOutcome {
        switch prepare(call.definition.name, call.arguments, call.caller) {
        case .refused(let reason): return ControlOutcome(result: .refusal(reason), notification: nil)
        case .ready(_, _, let perform): return perform()
        }
    }

    /// The user's answer. What they allowed is what they were shown: the change is prepared again
    /// against the state as it is now and must ask the very same question, and stand for the very
    /// same thing where the question cannot show it (its fingerprint), or nothing happens.
    /// An answer to a call that has already ended — the Windows mirror ends one whose dialog threw —
    /// changes nothing.
    private func answered(_ call: Call, _ request: ConfirmationRequest, _ fingerprint: String?, _ allowed: Bool) {
        guard !call.answered, !call.finished else { return }
        call.answered = true
        // The Windows mirror catches around what follows, as it does where a write starts; Swift's
        // closures here do not throw.
        var outcome = ControlOutcome(result: .refusal(ControlTools.declinedError), notification: nil)
        if allowed {
            // Decided again: the integration may have been turned off while the dialog was open, and
            // an Allow then must not reach past it.
            if case .refuse(let reason) = decision(call.definition, call.caller) {
                outcome = ControlOutcome(result: .refusal(reason), notification: nil)
            } else {
                switch prepare(call.definition.name, call.arguments, call.caller) {
                case .refused(let reason):
                    outcome = ControlOutcome(result: .refusal(reason), notification: nil)
                case .ready(let again, let againFingerprint, let perform):
                    // A fingerprint is hex digits, which == compares exactly on both platforms.
                    outcome = again == request && againFingerprint == fingerprint
                        ? perform()
                        : ControlOutcome(result: .refusal(ControlTools.changedWhileAskingError), notification: nil)
                }
            }
        }
        announce(call, outcome, unasked: false)
        finish(call, outcome.result)
    }

    /// Announces a change that landed when nobody else will hear of it: always for one made without
    /// asking, for a direct change that moved what Claude runs (`announces`), and for any other once
    /// its caller has already been told it is still waiting, or its connection has closed and it will
    /// never hear the answer, so a change Claude asked for never lands unannounced. The command line's
    /// own changes are the user's, and never announced. The tool's own notification says it, or else its
    /// answer; it is posted once, however many of these hold.
    private func announce(_ call: Call, _ outcome: ControlOutcome, unasked: Bool) {
        guard !outcome.result.isError, call.caller != .cli,
              unasked || outcome.announces || call.delivered || call.isAbandoned() else { return }
        // The Windows mirror catches around this: a C# notifier can throw, and Swift's does not. The
        // change has applied whatever the notifier does, so its answer stays a success.
        state.notifyClaudeChange(outcome.notification ?? outcome.result.text)
    }

    /// Answers the call, unless it already has its answer, and lets the next write start. A call
    /// ends once.
    private func finish(_ call: Call, _ result: ControlResult) {
        guard !call.finished else { return }
        call.finished = true
        // The Windows mirror lets the next write start even when the answer throws.
        deliver(call, result)
        writing = false
        startNextWrite()
    }

    private func deliver(_ call: Call, _ result: ControlResult) {
        guard !call.delivered else { return }
        call.delivered = true
        call.completion(result)
    }

    // MARK: Waiting

    /// Progress every `progressInterval` while a call waits — for its turn, or for the user's
    /// answer — and, at `confirmationWait`, the early answer that says so. The call carries on: its
    /// turn still comes, and the user's answer still counts. The command line is never answered
    /// early, and a call whose connection has closed hears nothing more and gets no early answer.
    private func startWaiting(_ call: Call) {
        guard !call.waiting, !call.delivered else { return }
        call.waiting = true
        scheduleTick(call)
    }

    private func scheduleTick(_ call: Call) {
        host.delay(ControlTools.progressInterval) { [weak self] in
            // A call whose connection has closed hears nothing more — no progress, no early answer — so
            // one still waiting its turn stays untold, and is dropped when its turn comes.
            guard let self, !call.delivered, !call.isAbandoned() else { return }
            call.ticks += 1
            let waited = Double(call.ticks) * ControlTools.progressInterval
            call.progress(waited)
            if waited >= ControlTools.confirmationWait, call.caller != .cli {
                // Only a call whose own dialog is open is waiting for an answer; any other is waiting
                // for its turn. The command line is never answered early: it is not bound by Claude's
                // time limit, and the person who ran it waits for the change itself.
                self.deliver(call, call.asking ? ControlTools.waitingResult : ControlTools.queuedResult)
            } else {
                self.scheduleTick(call)
            }
        }
    }

    /// One call on its way through. The Windows mirror names it `ToolCall`: C# cannot name a nested
    /// type after a method of its class.
    private final class Call {
        let definition: ControlToolDefinition
        let arguments: ControlArguments
        let caller: ControlCaller
        let progress: (Double) -> Void
        let completion: (ControlResult) -> Void
        let isAbandoned: () -> Bool
        /// The caller has its answer — perhaps the early one that says the user's is awaited.
        var delivered = false
        /// Its confirmation has been put to the user.
        var asking = false
        /// The user has answered the confirmation.
        var answered = false
        /// It has ended, and the next write may start.
        var finished = false
        var waiting = false
        var ticks = 0

        init(definition: ControlToolDefinition, arguments: ControlArguments, caller: ControlCaller,
             progress: @escaping (Double) -> Void, completion: @escaping (ControlResult) -> Void,
             isAbandoned: @escaping () -> Bool) {
            self.definition = definition
            self.arguments = arguments
            self.caller = caller
            self.progress = progress
            self.completion = completion
            self.isAbandoned = isAbandoned
        }
    }

    // MARK: Arguments

    /// The arguments checked against the tool's schema before anything runs: every required one
    /// present, every one present of its declared type and, where it has one, among its `enum`.
    /// Arguments the schema does not name are ignored. nil when they pass.
    static func argumentError(_ arguments: [String: JSONValue], _ schema: JSONValue) -> String? {
        guard case .object(let schemaObject) = schema else { return nil }
        if case .array(let required)? = schemaObject["required"] {
            for case .string(let name) in required where arguments[name] == nil {
                return missingArgumentError(name)
            }
        }
        guard case .object(let properties)? = schemaObject["properties"] else { return nil }
        for name in arguments.keys.sorted(by: { $0.ordinallyPrecedes($1) }) {
            guard let value = arguments[name], case .object(let property)? = properties[name],
                  case .string(let type)? = property["type"] else { continue }
            if !matches(value, type: type) { return wrongTypeError(name, kindWord(type)) }
            if case .array(let options)? = property["enum"], !options.contains(value) {
                let words = options.compactMap { option -> String? in
                    if case .string(let word) = option { return word }
                    return nil
                }
                return notOneOfError(name, words.joined(separator: ", "))
            }
        }
        return nil
    }

    static func matches(_ value: JSONValue, type: String) -> Bool {
        switch (type, value) {
        case ("string", .string), ("boolean", .bool), ("integer", .int), ("object", .object):
            return true
        case ("array", .array(let items)):
            return items.allSatisfy { item in
                if case .string = item { return true }
                return false
            }
        default:
            return false
        }
    }

    static func kindWord(_ type: String) -> String {
        switch type {
        case "boolean": return booleanKind
        case "integer": return integerKind
        case "array": return listKind
        case "object": return objectKind
        default: return textKind
        }
    }
}

/// A write, prepared against the state as it is now: refused with the reason, or ready — with the
/// question to ask when the tool asks, and the change itself. The Windows mirror has no enum
/// payloads, so its two steps are records.
///
/// `fingerprint`, never shown, stands for what the change would land where the question cannot
/// show it — a document's or a backup's bytes, whose secrets the dialog masks — so an Allow lands
/// only while that is unchanged too. The Windows mirror declares it last, where C# puts an
/// optional parameter.
enum ControlStep {
    case refused(String)
    case ready(ConfirmationRequest?, fingerprint: String? = nil, perform: () -> ControlOutcome)
}

/// What a performed write answers, and what a notification says about it when nobody was asked.
/// `announces`: a direct change that moved what Claude runs, which the user hears of although
/// nobody was asked and its caller has its answer.
struct ControlOutcome {
    let result: ControlResult
    let notification: String?
    var announces = false
}

/// A call's arguments by name. The schema check has already run, so a present argument has the
/// type its schema declares.
struct ControlArguments {
    let values: [String: JSONValue]

    func string(_ name: String) -> String? {
        if case .string(let text)? = values[name] { return text }
        return nil
    }

    func bool(_ name: String) -> Bool? {
        if case .bool(let flag)? = values[name] { return flag }
        return nil
    }

    func strings(_ name: String) -> [String]? {
        guard case .array(let items)? = values[name] else { return nil }
        return items.compactMap { item in
            if case .string(let text) = item { return text }
            return nil
        }
    }

    func object(_ name: String) -> JSONValue? {
        if case .object? = values[name] { return values[name] }
        return nil
    }
}
