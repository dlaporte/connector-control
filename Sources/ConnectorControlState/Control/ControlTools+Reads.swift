import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTools.Reads.cs
///
/// The read tools. None of them writes anything: the store, the settings, Claude's config and the
/// backups are byte-identical after any of them.
extension ControlTools {
    public static let activeCollectionLabel = "Active collection"
    public static let claudeLabel = "Claude"
    public static let integrationLabel = "Claude integration"
    public static let masterListLabel = "Master list"
    public static let problemLabel = "Problem"
    public static let claudeRunning = "running"
    public static let claudeRunningRestart = "running · restart required"
    public static let claudeNotRunning = "not running"
    public static let integrationOnConfirming = "on · confirmations on"
    public static let integrationOnNotConfirming = "on · confirmations off"
    public static let nameHeader = "NAME"
    public static let kindHeader = "KIND"
    public static let connectorsHeader = "CONNECTORS"
    public static let runsHeader = "RUNS"
    public static let idHeader = "ID"
    public static let collectionHeader = "COLLECTION"
    public static let activeMark = "active"
    public static let localKind = "local"
    public static let subscribedKind = "subscribed"
    public static let publishedKind = "published"
    public static let updateAvailableKind = "update available"
    public static let noBackupsText = "No backups yet."

    public static func statusTitle(_ version: String) -> String { "Connector Control \(version)" }

    public static func connectorCountText(_ total: Int, _ on: Int) -> String { "\(total) connectors, \(on) on" }

    public static func installHint(_ title: String, _ command: String) -> String { "\(title): \(command)" }

    public static func emptyCollectionText(_ collection: String) -> String { "“\(collection)” has no connectors." }

    public static func noConnectorError(_ name: String, _ collection: String) -> String { "There’s no connector named “\(name)” in “\(collection)”." }

    public static func notSubscribedError(_ collection: String) -> String { "“\(collection)” isn’t a subscribed collection." }

    public static func upToDateText(_ collection: String) -> String { "“\(collection)” is up to date." }

    func read(_ name: String, _ arguments: ControlArguments, _ caller: ControlCaller,
              _ done: @escaping (ControlResult) -> Void) {
        switch name {
        case "get_status":
            done(status())
        case "check_tools":
            checkTools(done)
        case "list_collections":
            done(listCollections())
        case "list_connectors":
            done(listConnectors(arguments.string("collection")))
        case "get_connector":
            // Secret values are the command-line tool's to show, never Claude's.
            done(connector(arguments.string("name") ?? "", in: arguments.string("collection"),
                           reveal: arguments.bool("reveal") == true && caller == .cli))
        case "get_pending_update":
            done(pendingUpdate(arguments.string("collection") ?? ""))
        case "list_backups":
            done(listBackups())
        case "get_settings":
            done(currentSettings())
        default:
            done(.refusal(ControlTools.unknownToolError(name)))
        }
    }

    func status() -> ControlResult {
        let flags = state.settings
        let running = state.isClaudeRunning
        let claude = !running ? ControlTools.claudeNotRunning
            : state.needsClaudeRestart ? ControlTools.claudeRunningRestart : ControlTools.claudeRunning
        let integration = !flags.claudeIntegrationEnabled ? ControlTools.offWord
            : flags.confirmClaudeChanges ? ControlTools.integrationOnConfirming : ControlTools.integrationOnNotConfirming
        let masterList = state.service.paths.masterStoreURL.path
        var rows = [
            (ControlTools.activeCollectionLabel,
             ControlTools.detail(state.activeCollection,
                                 ControlTools.connectorCountText(state.store.mcps.count, state.store.enabledServers.count))),
            (ControlTools.claudeLabel, claude),
            (ControlTools.integrationLabel, integration),
            (ControlTools.masterListLabel, masterList),
        ]
        if let problem = state.lastError { rows.append((ControlTools.problemLabel, problem)) }
        let structured: JSONValue = .object([
            "version": .string(version),
            "activeCollection": .string(state.activeCollection),
            "connectors": .int(state.store.mcps.count),
            "enabled": .int(state.store.enabledServers.count),
            "claude": .object(["running": .bool(running), "restartRequired": .bool(state.needsClaudeRestart)]),
            "integration": .object(["enabled": .bool(flags.claudeIntegrationEnabled),
                                    "confirmChanges": .bool(flags.confirmClaudeChanges)]),
            "masterList": .string(masterList),
            "applyRetryNeeded": .bool(state.applyRetryNeeded),
            "problem": state.lastError.map(JSONValue.string) ?? .null,
        ])
        return ControlResult(structured: structured,
                             text: ([ControlTools.statusTitle(version)] + ControlText.labelled(rows)).joined(separator: "\n"),
                             isError: false)
    }

    /// Probes all four launchers again — the user may have installed one since — and answers once
    /// every result is in.
    func checkTools(_ done: @escaping (ControlResult) -> Void) {
        state.refreshTools()
        state.whenToolsProbed { [weak self] in
            guard let self else { return }
            done(self.toolsResult())
        }
    }

    /// Each launcher as the app's own words put it — "Found", "Not found" or, on the Mac, "Not visible
    /// to Claude Desktop"; never a version, so the column reads the same whatever is installed — with
    /// where it is or how to install it.
    func toolsResult() -> ControlResult {
        var rows: [[String]] = []
        var items: [JSONValue] = []
        for tool in Tool.allCases {
            let status = state.toolStatuses[tool]
            var item: [String: JSONValue] = ["name": .string(tool.name)]
            let word: String
            let detail: String
            switch status {
            case .found(let path, _):
                item["status"] = .string("found")
                item["path"] = .string(path)
                word = ToolNote.foundText
                detail = path
            case .foundInShellOnly(let path, _):
                // macOS only: the Windows probe has no shell-only state.
                item["status"] = .string("shellOnly")
                item["path"] = .string(path)
                word = ToolNote.shellOnlyStatusText
                detail = path
            case .notFound, nil:
                item["status"] = .string("notFound")
                word = ToolNote.notFoundText
                detail = ControlTools.installHint(tool.family.linkTitle, tool.family.installCommand)
            }
            rows.append([tool.name, word, detail])
            items.append(.object(item))
        }
        return ControlResult(structured: .object(["tools": .array(items)]),
                             text: ControlText.table(header: nil, rows: rows).joined(separator: "\n"), isError: false)
    }

    func listCollections() -> ControlResult {
        var rows: [[String]] = []
        var items: [JSONValue] = []
        for name in state.collectionNames {
            let mcps = state.store.collections[name]?.mcps ?? [:]
            let enabled = mcps.values.filter(\.enabled).count
            let synced = state.isSynced(name)
            let published = state.isPublished(name)
            let pending = state.pendingUpdates[name] != nil
            var kind = synced ? ControlTools.subscribedKind : ControlTools.localKind
            if published { kind = ControlTools.detail(kind, ControlTools.publishedKind) }
            if pending { kind = ControlTools.detail(kind, ControlTools.updateAvailableKind) }
            let count = ControlTools.connectorCountText(mcps.count, enabled)
            rows.append([name == state.activeCollection ? ControlTools.activeMark : "", name, kind, count])
            items.append(.object([
                "name": .string(name),
                "active": .bool(name == state.activeCollection),
                "kind": .string(synced ? "subscribed" : "local"),
                "published": .bool(published),
                "updateAvailable": .bool(pending),
                "connectors": .int(mcps.count),
                "enabled": .int(enabled),
            ]))
        }
        let header = ["", ControlTools.nameHeader, ControlTools.kindHeader, ControlTools.connectorsHeader]
        return ControlResult(structured: .object(["active": .string(state.activeCollection), "collections": .array(items)]),
                             text: ControlText.table(header: header, rows: rows).joined(separator: "\n"), isError: false)
    }

    func listConnectors(_ requested: String?) -> ControlResult {
        let collection = requested ?? state.activeCollection
        guard let held = state.store.collections[collection] else { return .refusal(MasterStore.noCollectionError(collection)) }
        var rows: [[String]] = []
        var items: [JSONValue] = []
        for name in held.mcps.keys.sorted(by: { $0.ordinallyPrecedes($1) }) {
            guard let entry = held.mcps[name] else { continue }
            let runs = CollectionsModel.target(of: entry.config)
            rows.append([entry.enabled ? ControlTools.onWord : ControlTools.offWord, name, runs])
            items.append(.object([
                "name": .string(name),
                "enabled": .bool(entry.enabled),
                "runs": .string(runs),
                "caution": state.connectorCaution(name, in: collection).map(JSONValue.string) ?? .null,
            ]))
        }
        let text = rows.isEmpty
            ? ControlTools.emptyCollectionText(collection)
            : ControlText.table(header: ["", ControlTools.nameHeader, ControlTools.runsHeader], rows: rows).joined(separator: "\n")
        return ControlResult(structured: .object(["collection": .string(collection), "connectors": .array(items)]),
                             text: text, isError: false)
    }

    func connector(_ name: String, in requested: String?, reveal: Bool) -> ControlResult {
        let collection = requested ?? state.activeCollection
        guard let held = state.store.collections[collection] else { return .refusal(MasterStore.noCollectionError(collection)) }
        guard let entry = held.mcps[name] else { return .refusal(ControlTools.noConnectorError(name, collection)) }
        let heading = ControlTools.detail(ControlTools.detail(name, collection),
                                          entry.enabled ? ControlTools.onWord : ControlTools.offWord)
        let structured: JSONValue = .object([
            "name": .string(name),
            "collection": .string(collection),
            "enabled": .bool(entry.enabled),
            "config": ControlMasking.maskedConfig(entry.config, reveal: reveal),
            "needs": .array(Placeholder.unfilledNames(in: entry.config).map(JSONValue.string)),
            "caution": state.connectorCaution(name, in: collection).map(JSONValue.string) ?? .null,
        ])
        return ControlResult(structured: structured,
                             text: ([heading] + ControlMasking.codeLines(entry.config, exposure: reveal ? .revealed : .masked)).joined(separator: "\n"),
                             isError: false)
    }

    func pendingUpdate(_ collection: String) -> ControlResult {
        guard state.store.collections[collection] != nil else { return .refusal(MasterStore.noCollectionError(collection)) }
        guard state.isSynced(collection) else { return .refusal(ControlTools.notSubscribedError(collection)) }
        if let problem = state.sourceErrors[collection] { return .refusal(problem) }
        let diff = state.pendingUpdates[collection]
        let structured: JSONValue = .object([
            "collection": .string(collection),
            "added": .array((diff?.added ?? []).map(JSONValue.string)),
            "removed": .array((diff?.removed ?? []).map(JSONValue.string)),
            "changed": .array((diff?.changed ?? []).map(JSONValue.string)),
        ])
        let text = diff.map { AppState.collectionUpdateBanner(collection, $0.summary()) } ?? ControlTools.upToDateText(collection)
        return ControlResult(structured: structured, text: text, isError: false)
    }

    /// The backups the Restore sheet lists: newest first, the first-run original last.
    func backupURLs() throws -> [URL] {
        var found = try state.service.backups.backups(series: RestoreModel.series)
        if let original = state.service.backups.originalSnapshotURL(series: RestoreModel.series) { found.append(original) }
        return found
    }

    func listBackups() -> ControlResult {
        let urls: [URL]
        do {
            urls = try backupURLs()
        } catch {
            return .refusal(AppState.friendly(error))
        }
        let backupsDir = state.service.paths.backupsDirURL
        var rows: [[String]] = []
        var items: [JSONValue] = []
        for url in urls {
            let collection = BackupCollections.collection(of: url, in: backupsDir)
            rows.append([url.lastPathComponent, collection ?? ""])
            items.append(.object(["id": .string(url.lastPathComponent), "collection": collection.map(JSONValue.string) ?? .null]))
        }
        let text = rows.isEmpty
            ? ControlTools.noBackupsText
            : ControlText.table(header: [ControlTools.idHeader, ControlTools.collectionHeader], rows: rows).joined(separator: "\n")
        return ControlResult(structured: .object(["backups": .array(items)]), text: text, isError: false)
    }

    func currentSettings() -> ControlResult {
        // Launch at login is read fresh, as the General tab reads it whenever it appears: the user
        // may have changed it outside the app since.
        settings.refresh()
        var values: [String: JSONValue] = [:]
        var rows: [(String, String)] = []
        for key in ControlSettingKey.all {
            let value = settingValue(key)
            values[key] = value
            rows.append((key, ControlTools.settingText(value)))
        }
        return ControlResult(structured: .object(["settings": .object(values)]),
                             text: ControlText.labelled(rows).joined(separator: "\n"), isError: false)
    }

    func settingValue(_ key: String) -> JSONValue {
        switch key {
        case ControlSettingKey.launchAtLogin: return .bool(settings.launchAtLogin)
        case ControlSettingKey.confirmRestart: return .bool(settings.confirmBeforeRestart)
        case ControlSettingKey.confirmQuit: return .bool(settings.confirmBeforeQuit)
        case ControlSettingKey.notifyExternalChanges: return .bool(settings.notifyExternalChanges)
        case ControlSettingKey.automaticUpdates: return .bool(settings.autoUpdate)
        case ControlSettingKey.backupsToKeep: return .int(settings.backupKeepCount)
        case ControlSettingKey.claudeIntegration: return .bool(settings.claudeIntegrationEnabled)
        case ControlSettingKey.confirmClaudeChanges: return .bool(settings.confirmClaudeChanges)
        default: return .null
        }
    }

    static func settingText(_ value: JSONValue) -> String {
        switch value {
        case .bool(let on): return on ? onWord : offWord
        case .int(let number): return String(number)
        default: return ""
        }
    }
}
