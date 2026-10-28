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
/// every `progressInterval` and, at `confirmationWait`, that the answer is still awaited.
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
    /// The column `help` starts its summaries in.
    public static let helpColumn = 32
    /// Seconds between progress reports while a change waits.
    public static let progressInterval: TimeInterval = 5
    /// How long a call waits before it answers `waitingText`: under Claude Desktop's tool-call
    /// timeout, which is what this constant is tuned to.
    public static let confirmationWait: TimeInterval = 50

    public static func unknownToolError(_ name: String) -> String { "There’s no tool named “\(name)”." }

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

    public var definitions: [ControlToolDefinition] { ControlTools.catalog }

    /// The running app's version: `get_status` says it, and the MCP session introduces itself with it.
    public var version: String { settings.version }

    /// Runs one tool for `caller`. `progress` reports how many seconds a change has waited;
    /// `completion` is called once.
    public func call(name: String, arguments: JSONValue, caller: ControlCaller,
                     progress: @escaping (Double) -> Void, completion: @escaping (ControlResult) -> Void) {
        guard let definition = ControlTools.catalog.first(where: { $0.name == name }) else {
            completion(.refusal(ControlTools.unknownToolError(name)))
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
                        progress: progress, completion: completion)
        guard definition.gate != .read else {
            read(definition.name, call.arguments, caller) { [weak self] result in self?.deliver(call, result) }
            return
        }
        writeQueue.append(call)
        if writing { startWaiting(call) } else { startNextWrite() }
    }

    /// `help`: every tool's command in its group, with the first sentence of its description.
    public func helpText() -> String {
        var lines = [ControlTools.helpUsage]
        for group in ControlTools.helpGroups {
            var rows = ControlTools.catalog.filter { $0.cliGroup == group }.map { ($0.cliUsage, $0.summary) }
            if group == ControlTools.claudeGroup { rows.append((ControlTools.relayUsage, ControlTools.relaySummary)) }
            guard !rows.isEmpty else { continue }
            lines.append(group)
            lines += rows.map { usage, summary in
                "  " + ControlText.pad(usage, to: max(ControlTools.helpColumn, ControlText.width(usage) + 2)) + summary
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
            case .ready(let request, _):
                // Every tool that asks builds its question; one that did not is refused rather than
                // run unasked.
                guard let request else {
                    finish(call, .refusal(ControlTools.noQuestionError))
                    return
                }
                call.asking = true
                confirmer.confirm(request) { [weak self] allowed in self?.answered(call, request, allowed) }
                if !call.answered { startWaiting(call) }
            }
        }
    }

    /// Prepares and performs a write that does not ask.
    private func run(_ call: Call) -> ControlOutcome {
        switch prepare(call.definition.name, call.arguments, call.caller) {
        case .refused(let reason): return ControlOutcome(result: .refusal(reason), notification: nil)
        case .ready(_, let perform): return perform()
        }
    }

    /// The user's answer. What they allowed is what they were shown: the change is prepared again
    /// against the state as it is now and must ask the very same question, or nothing happens.
    /// An answer to a call that has already ended — the Windows mirror ends one whose dialog threw —
    /// changes nothing.
    private func answered(_ call: Call, _ request: ConfirmationRequest, _ allowed: Bool) {
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
                case .ready(let again, let perform):
                    outcome = again == request
                        ? perform()
                        : ControlOutcome(result: .refusal(ControlTools.changedWhileAskingError), notification: nil)
                }
            }
        }
        announce(call, outcome, unasked: false)
        finish(call, outcome.result)
    }

    /// Announces a change that landed when nobody else will hear of it: always for one made without
    /// asking, and for any other once its caller has already been told it is still waiting, so a
    /// change Claude asked for never lands unannounced. The command line's own changes are the
    /// user's, and never announced. The tool's own notification says it, or else its answer.
    private func announce(_ call: Call, _ outcome: ControlOutcome, unasked: Bool) {
        guard !outcome.result.isError, call.caller != .cli, unasked || call.delivered else { return }
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
    /// turn still comes, and the user's answer still counts.
    private func startWaiting(_ call: Call) {
        guard !call.waiting, !call.delivered else { return }
        call.waiting = true
        scheduleTick(call)
    }

    private func scheduleTick(_ call: Call) {
        host.delay(ControlTools.progressInterval) { [weak self] in
            guard let self, !call.delivered else { return }
            call.ticks += 1
            let waited = Double(call.ticks) * ControlTools.progressInterval
            call.progress(waited)
            if waited >= ControlTools.confirmationWait {
                // Only a call whose own dialog is open is waiting for an answer; any other is
                // waiting for its turn, and the command line, never asked, is always the latter.
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
             progress: @escaping (Double) -> Void, completion: @escaping (ControlResult) -> Void) {
            self.definition = definition
            self.arguments = arguments
            self.caller = caller
            self.progress = progress
            self.completion = completion
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
enum ControlStep {
    case refused(String)
    case ready(ConfirmationRequest?, perform: () -> ControlOutcome)
}

/// What a performed write answers, and what a notification says about it when nobody was asked.
struct ControlOutcome {
    let result: ControlResult
    let notification: String?
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
