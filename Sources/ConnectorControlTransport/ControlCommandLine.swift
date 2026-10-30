import Foundation
import ConnectorControlCore
import ConnectorControlState

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlCommandLine.cs
///
/// What `connector-control` was asked to do. The Windows mirror has no enum payloads, so its cases are
/// records; `arguments` is a JSON object, which both platforms compare by value.
public enum ControlInvocation: Equatable, Sendable {
    case help
    case version
    case relay
    case call(tool: String, arguments: JSONValue, json: Bool)
    /// Nothing the tool can run: the sentence to print, and exit 2.
    case usage(String)
}

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlCommandLine.cs
///
/// `connector-control`'s arguments, read into what to run: one command per tool, as `help` lists them,
/// or `help`, `--version` and the relay. `--json` anywhere asks for the structure Claude receives
/// instead of text — except in `add` and `edit`, where `--json` followed by a JSON object (its first
/// character not a space is "{") is the connector's config. Words after `--` are names. Paths are made
/// full here, against the directory the command runs in, because the app runs elsewhere.
@MainActor
public enum ControlCommandLine {
    public static let jsonFlag = "--json"
    public static let versionFlag = "--version"
    public static let helpWords: Set<String> = ["help", "--help", "-h"]
    public static let endOfOptions = "--"
    static let collection = "--collection"
    static let file = "--file"
    static let rename = "--rename"
    static let into = "--into"
    static let new = "--new"
    static let onClash = "--on-clash"
    static let nameOption = "--name"
    static let reveal = "--reveal"
    static let off = "--off"

    public static func unknownCommandError(_ command: String) -> String { "There’s no command named “\(command)”. Run connector-control help to see them all." }

    public static func usageError(_ usage: String) -> String { "Usage: connector-control \(usage)" }

    /// Between the usages of a command group, so each starts under the first.
    public static let usageContinuation = "\n       connector-control "

    public static func unknownSubcommandError(_ subcommand: String, _ group: String) -> String { "“\(subcommand)” isn’t a \(group) command." }

    public static let helpTakesNoArgumentsError = "help takes no arguments. Run connector-control help to see every command."

    public static func unknownOptionError(_ option: String) -> String { "“\(option)” isn’t an option of this command." }

    public static func missingValueError(_ option: String) -> String { "“\(option)” needs a value." }

    public static func versionLine(_ version: String) -> String { "connector-control \(version)" }

    /// Reads `arguments`, the program's name left out. `workingDirectory` makes a relative path full;
    /// `readFile` reads `--file`.
    public static func parse(_ arguments: [String], workingDirectory: String,
                             readFile: (String) throws -> Data) -> ControlInvocation {
        var words = arguments[...]
        var leadingJSON = false
        while words.first == jsonFlag {
            leadingJSON = true
            words = words.dropFirst()
        }
        guard let command = words.first else { return .usage(ControlTools.helpText()) }
        let rest = words.dropFirst()
        // --version wherever --json sits: asking for the structure changes nothing about the version.
        if arguments.filter({ $0 != jsonFlag }) == [versionFlag] { return .version }
        if helpWords.contains(command) { return rest.isEmpty ? .help : .usage(helpTakesNoArgumentsError) }
        if command == ClaudeIntegration.relayArgument {
            return rest.first.map { ControlInvocation.usage(unknownOptionError($0)) } ?? .relay
        }
        do {
            let invocation = try commandCall(command, rest, workingDirectory: workingDirectory, readFile: readFile)
            if leadingJSON, case .call(let tool, let arguments, _) = invocation { return .call(tool: tool, arguments: arguments, json: true) }
            return invocation
        } catch let error as UsageError {
            return .usage(error.message)
        } catch {
            return .usage(AppState.friendly(error))
        }
    }

    struct UsageError: Error {
        let message: String
    }

    /// A command's words after its name.
    struct Words {
        var positionals: [String] = []
        var options: [String: String] = [:]
        var flags: Set<String> = []
        var json = false
        /// `add` and `edit`'s config, given after `--json`.
        var config: String?
    }

    static func commandCall(_ command: String, _ rest: ArraySlice<String>, workingDirectory: String,
                            readFile: (String) throws -> Data) throws -> ControlInvocation {
        func full(_ path: String) -> String { fullPath(path, workingDirectory) }
        func configValue(_ words: Words, required: Bool, tool: String) throws -> JSONValue? {
            try config(words, required: required, tool: tool, workingDirectory: workingDirectory, readFile: readFile)
        }
        switch command {
        case "status": return try call("get_status", rest)
        case "doctor": return try call("check_tools", rest)
        case "connectors":
            return try call("list_connectors", rest, options: [collection]) { ["collection": text($0.options[collection])] }
        case "show":
            return try call("get_connector", rest, options: [collection], flags: [reveal], count: 1...1) {
                ["name": .string($0.positionals[0]), "collection": text($0.options[collection]),
                 "reveal": $0.flags.contains(reveal) ? .bool(true) : nil]
            }
        case "enable", "disable":
            return try call("set_connector_enabled", rest, count: 1...1) {
                ["name": .string($0.positionals[0]), "enabled": .bool(command == "enable")]
            }
        case "add":
            return try call("add_connector", rest, options: [collection, file], flags: [off], takesConfig: true, count: 1...1) {
                ["name": .string($0.positionals[0]), "config": try configValue($0, required: true, tool: "add_connector"),
                 "collection": text($0.options[collection]), "enabled": $0.flags.contains(off) ? .bool(false) : nil]
            }
        case "edit":
            return try call("update_connector", rest, options: [collection, file, rename], takesConfig: true, count: 1...1) {
                ["name": .string($0.positionals[0]), "config": try configValue($0, required: false, tool: "update_connector"),
                 "newName": text($0.options[rename]), "collection": text($0.options[collection])]
            }
        case "delete":
            return try call("delete_connectors", rest, options: [collection], count: 1...Int.max) {
                ["names": .array($0.positionals.map(JSONValue.string)), "collection": text($0.options[collection])]
            }
        case "collections": return try call("list_collections", rest)
        case "use":
            return try call("switch_collection", rest, count: 1...1) { ["collection": .string($0.positionals[0])] }
        case "collection":
            let more = rest.dropFirst()
            switch rest.first ?? "" {
            case "new": return try call("create_collection", more, count: 1...1) { ["name": .string($0.positionals[0])] }
            case "rename":
                return try call("rename_collection", more, count: 2...2) {
                    ["collection": .string($0.positionals[0]), "newName": .string($0.positionals[1])]
                }
            case "duplicate":
                return try call("duplicate_collection", more, count: 2...2) {
                    ["collection": .string($0.positionals[0]), "newName": .string($0.positionals[1])]
                }
            case "delete": return try call("delete_collection", more, count: 1...1) { ["collection": .string($0.positionals[0])] }
            default:
                let usages = ControlTools.catalog.map(\.cliUsage).filter { $0.hasPrefix(command + " ") }
                let usage = usageError(usages.joined(separator: usageContinuation))
                return .usage(rest.first.map { unknownSubcommandError($0, command) + "\n" + usage } ?? usage)
            }
        case "import":
            return try call("import_document", rest, options: [into, new, onClash], count: 1...1) {
                ["path": .string(full($0.positionals[0])), "into": text($0.options[into]),
                 "newCollection": text($0.options[new]), "onClash": text($0.options[onClash])]
            }
        case "subscribe":
            return try call("subscribe", rest, options: [nameOption], count: 1...1) {
                ["path": .string(full($0.positionals[0])), "name": text($0.options[nameOption])]
            }
        case "updates":
            return try call("get_pending_update", rest, count: 1...1) { ["collection": .string($0.positionals[0])] }
        case "apply-update":
            return try call("apply_update", rest, count: 1...1) { ["collection": .string($0.positionals[0])] }
        case "refresh":
            return try call("refresh_subscription", rest, count: 1...1) { ["collection": .string($0.positionals[0])] }
        case "settings":
            guard rest.first == "set" else { return try call("get_settings", rest) }
            return try call("set_setting", rest.dropFirst(), count: 2...2) {
                ["key": .string($0.positionals[0]), "value": .string($0.positionals[1])]
            }
        case "backups":
            guard rest.first == "restore" else { return try call("list_backups", rest) }
            return try call("restore_backup", rest.dropFirst(), count: 1...1) { ["id": .string($0.positionals[0])] }
        case "restart-claude": return try call("restart_claude", rest)
        default: return .usage(unknownCommandError(command))
        }
    }

    /// One command: the tool it runs, what it takes, and how its words become the tool's arguments
    /// (an argument left nil is left out).
    static func call(_ tool: String, _ words: ArraySlice<String>, options: Set<String> = [], flags: Set<String> = [],
                     takesConfig: Bool = false, count: ClosedRange<Int> = 0...0,
                     _ build: (Words) throws -> [String: JSONValue?] = { _ in [:] }) throws -> ControlInvocation {
        let read = try read(words, options: options, flags: flags, takesConfig: takesConfig)
        guard count.contains(read.positionals.count) else { throw UsageError(message: usageError(usage(of: tool))) }
        var arguments: [String: JSONValue] = [:]
        for (key, value) in try build(read) {
            if let value { arguments[key] = value }
        }
        return .call(tool: tool, arguments: .object(arguments), json: read.json)
    }

    static func read(_ words: ArraySlice<String>, options: Set<String>, flags: Set<String>, takesConfig: Bool) throws -> Words {
        var read = Words()
        var index = words.startIndex
        var namesOnly = false
        while index < words.endIndex {
            let word = words[index]
            index += 1
            // On UTF-8, not characters: a combining mark after the dashes would join the second one into a
            // single character that is no longer "--", where the Windows mirror reads the two dashes.
            if namesOnly || !word.utf8.starts(with: "--".utf8) {
                read.positionals.append(word)
            } else if word == endOfOptions {
                namesOnly = true
            } else if word == jsonFlag {
                if takesConfig, read.config == nil, index < words.endIndex, startsWithBrace(words[index]) {
                    read.config = words[index]
                    index += 1
                } else {
                    read.json = true
                }
            } else if flags.contains(word) {
                read.flags.insert(word)
            } else if options.contains(word) {
                guard index < words.endIndex else { throw UsageError(message: missingValueError(word)) }
                read.options[word] = words[index]
                index += 1
            } else {
                throw UsageError(message: unknownOptionError(word))
            }
        }
        return read
    }

    /// Whether `word`'s first character that is not a space, tab or line break is "{". Platform idiom:
    /// the four ASCII spaces only, which both platforms read alike.
    static func startsWithBrace(_ word: String) -> Bool {
        word.unicodeScalars.first { !" \t\r\n".unicodeScalars.contains($0) } == "{"
    }

    /// `add` and `edit`'s config: after `--json`, or read from `--file`; not both.
    static func config(_ words: Words, required: Bool, tool: String, workingDirectory: String,
                       readFile: (String) throws -> Data) throws -> JSONValue? {
        let path = words.options[file]
        if words.config != nil, path != nil { throw UsageError(message: usageError(usage(of: tool))) }
        if let inline = words.config { return try parseConfig(Data(inline.utf8)) }
        if let path {
            let full = fullPath(path, workingDirectory)
            let data: Data
            do {
                data = try readFile(full)
            } catch {
                throw UsageError(message: AppState.sourceUnreadableError(URL(fileURLWithPath: full).lastPathComponent,
                                                                         AppState.friendly(error)))
            }
            return try parseConfig(data)
        }
        if required { throw UsageError(message: usageError(usage(of: tool))) }
        return nil
    }

    static func parseConfig(_ data: Data) throws -> JSONValue {
        guard let value = try? JSONValue.parse(data) else { throw UsageError(message: EditorModel.notValidJSON) }
        return value
    }

    /// A path as the app must read it. Platform idiom: a file URL relative to the working directory,
    /// standardised; the Windows mirror's Path.GetFullPath(path, basePath).
    static func fullPath(_ path: String, _ workingDirectory: String) -> String {
        URL(fileURLWithPath: path, relativeTo: URL(fileURLWithPath: workingDirectory, isDirectory: true)).standardizedFileURL.path
    }

    static func text(_ value: String?) -> JSONValue? { value.map(JSONValue.string) }

    static func usage(of tool: String) -> String { ControlTools.catalog.first { $0.name == tool }?.cliUsage ?? tool }
}

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlCommandLine.cs (ControlOutput)
///
/// What the command-line tool prints and how it exits: the app's text for people, or the structure
/// Claude receives under `--json`; a refusal's sentence on stderr.
public struct ControlOutput: Equatable, Sendable {
    public static let doneExit: Int32 = 0
    public static let refusedExit: Int32 = 1
    public static let usageExit: Int32 = 2
    public static let unreachableExit: Int32 = 3

    public let stdout: String?
    public let stderr: String?
    public let exitCode: Int32

    public init(stdout: String?, stderr: String?, exitCode: Int32) {
        self.stdout = stdout
        self.stderr = stderr
        self.exitCode = exitCode
    }

    public static func usage(_ message: String) -> ControlOutput { ControlOutput(stdout: nil, stderr: message, exitCode: usageExit) }

    public static func unreachable(_ message: String) -> ControlOutput {
        ControlOutput(stdout: nil, stderr: message, exitCode: unreachableExit)
    }

    /// The app's answer to the command's call. A refusal, or an error the session answered instead,
    /// exits 1 with its sentence on stderr — or as `{"error": …}` on stdout under `json`.
    public static func answer(_ response: JSONValue, json: Bool) -> ControlOutput {
        guard case .object(let fields) = response else { return refused(McpSession.internalErrorMessage, json: json) }
        if case .object(let result)? = fields["result"] {
            let structured = result["structuredContent"] ?? .object([:])
            var text = ""
            if case .array(let content)? = result["content"], case .object(let first)? = content.first,
               case .string(let words)? = first["text"] {
                text = words
            }
            let isRefusal = result["isError"] == .bool(true)
            if json { return ControlOutput(stdout: structured.compactLine(escapingSlashes: false), stderr: nil, exitCode: isRefusal ? refusedExit : doneExit) }
            return isRefusal ? ControlOutput(stdout: nil, stderr: text, exitCode: refusedExit)
                : ControlOutput(stdout: text, stderr: nil, exitCode: doneExit)
        }
        var message = McpSession.internalErrorMessage
        if case .object(let error)? = fields["error"], case .string(let said)? = error["message"] { message = said }
        return refused(message, json: json)
    }

    static func refused(_ message: String, json: Bool) -> ControlOutput {
        json ? ControlOutput(stdout: JSONValue.object(["error": .string(message)]).compactLine(escapingSlashes: false), stderr: nil, exitCode: refusedExit)
            : ControlOutput(stdout: nil, stderr: message, exitCode: refusedExit)
    }
}
