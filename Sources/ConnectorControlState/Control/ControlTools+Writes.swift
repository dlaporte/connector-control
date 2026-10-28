import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTools.Writes.cs
///
/// The write tools that need no question, because each is the user's own kind of change: switching a
/// connector or a collection, managing collections, refreshing a subscription, changing a setting.
/// Some change what Claude runs and say so, with a restart when one is due; the rest touch only this
/// app's own lists and settings. They never ask, and say what they did and what follows from it.
///
/// A direct change's answer is also what announces it when it lands after its caller was told it
/// was still waiting, so it names connectors and collections as the gated tools' questions do:
/// through `ControlText.escapedName(_:)`, since Claude chooses them.
extension ControlTools {
    public static let restartToUseText = "Restart Claude to use it."
    public static let restartToApplyText = "Restart Claude to apply the change."
    public static let keepCountError = "backups-to-keep takes a number from 5 to 100."

    public static func savedNotAppliedText(_ message: String) -> String { "Saved, but Claude’s config wasn’t updated: \(message)" }

    public static func turnedOnText(_ name: String, _ collection: String) -> String { "Turned on “\(name)” in “\(collection)”." }

    public static func turnedOffText(_ name: String, _ collection: String) -> String { "Turned off “\(name)” in “\(collection)”." }

    public static func switchedText(_ collection: String) -> String { "Switched to “\(collection)”." }

    public static func createdText(_ collection: String) -> String { "Created “\(collection)”." }

    public static func renamedCollectionText(_ old: String, _ new: String) -> String { "Renamed the collection “\(old)” to “\(new)”." }

    public static func duplicatedText(_ source: String, _ copy: String) -> String { "Copied “\(source)” as “\(copy)”." }

    public static func duplicateSubscribedError(_ collection: String) -> String { "“\(collection)” is subscribed; copy it in Connector Control with Make Local Copy." }

    public static func settingChangedText(_ key: String, _ value: String) -> String { "\(key) is now \(value)." }

    public static func integrationSetText(_ state: String) -> String { "Claude integration is \(state)." }

    public static func settingNotForClaudeError(_ key: String) -> String { "Only you can change \(key), in Connector Control or with the command-line tool." }

    public static func switchValueError(_ key: String) -> String { "\(key) takes on or off." }

    /// Prepares the write `name` against the state as it is now: every refusal is found here, before
    /// anyone is asked, and the change is performed only by the step's `perform`.
    func prepare(_ name: String, _ arguments: ControlArguments, _ caller: ControlCaller) -> ControlStep {
        if let prepareOverride { return prepareOverride(name, arguments, caller) }
        switch name {
        case "set_connector_enabled": return setEnabled(arguments)
        case "switch_collection": return switchCollection(arguments)
        case "create_collection": return createCollection(arguments)
        case "rename_collection": return renameCollection(arguments)
        case "duplicate_collection": return duplicateCollection(arguments)
        case "refresh_subscription": return refreshSubscription(arguments)
        case "set_setting": return setSetting(arguments, caller)
        case "add_connector": return addConnector(arguments, caller.askerName)
        case "update_connector": return updateConnector(arguments, caller.askerName)
        case "delete_connectors": return deleteConnectors(arguments, caller.askerName)
        case "delete_collection": return deleteCollection(arguments, caller.askerName)
        case "import_document": return importDocument(arguments, caller.askerName)
        case "subscribe": return subscribeDocument(arguments, caller.askerName)
        case "apply_update": return applyUpdate(arguments, caller.askerName)
        case "restore_backup": return restoreBackup(arguments, caller.askerName)
        case "restart_claude": return restartClaude(caller.askerName)
        default: return .refused(ControlTools.unknownToolError(name))
        }
    }

    /// A change refused as it was made, with the app's own sentence: through
    /// `ControlText.escaped(_:naming:)`, since the sentence may echo `names`, which others chose.
    static func refused(_ text: String, naming names: [String] = []) -> ControlOutcome {
        ControlOutcome(result: .refusal(ControlText.escaped(text, naming: names)), notification: nil)
    }

    /// A write's answer: what happened, then what follows for the collection Claude runs. A write
    /// that changed what Claude's config holds (`appliedServers` moved off `mark`, taken before the
    /// write) while Claude runs an older config says to restart; one that could not write it says
    /// the change was saved and gives the app's own message. `collection` nil is a write that
    /// reaches Claude whatever is active; `touchesClaude` false is one that cannot reach it at all. The notification, for a change nobody was asked about,
    /// carries the same restart sentence.
    func done(_ lines: [String], _ fields: [String: JSONValue], touching collection: String?,
              since mark: [String: JSONValue], restart: String, notification: String? = nil,
              touchesClaude: Bool = true) -> ControlOutcome {
        var text = lines
        // A write that never touches Claude's config (a window's setting) must not report a retry
        // an earlier, unrelated write left pending.
        let reachesClaude = touchesClaude && (collection == nil || collection == state.activeCollection)
        // Platform idiom: compared scalar by scalar, as the Windows mirror compares ordinally; Swift's
        // == holds canonically equivalent text equal, and would miss a change only in how a
        // character is composed.
        let applied = !ControlTools.identical(.object(state.appliedServers), .object(mark))
        var then: String?
        if reachesClaude, state.applyRetryNeeded {
            text.append(ControlTools.savedNotAppliedText(state.lastError ?? ""))
        } else if applied, state.needsClaudeRestart {
            // A one-sentence result takes the restart on the same line, as "Turned on “x” in “y”.
            // Restart Claude to use it." reads; a longer one gives it a line of its own.
            then = restart
            if text.count == 1 { text[0] += " " + restart } else { text.append(restart) }
        }
        var structured = fields
        structured["restartRequired"] = .bool(applied && state.needsClaudeRestart)
        structured["applied"] = .bool(!(reachesClaude && state.applyRetryNeeded))
        return ControlOutcome(
            result: ControlResult(structured: .object(structured), text: text.joined(separator: "\n"), isError: false),
            notification: notification.map { note in then.map { note + " " + $0 } ?? note })
    }

    func setEnabled(_ arguments: ControlArguments) -> ControlStep {
        let name = arguments.string("name") ?? ""
        let on = arguments.bool("enabled") ?? true
        // The active collection only, as the popover's switches act: it is the one Claude runs.
        let collection = state.activeCollection
        let (shownName, shownCollection) = (ControlText.escapedName(name), ControlText.escapedName(collection))
        guard state.store.collections[collection]?.mcps[name] != nil else {
            return .refused(ControlTools.noConnectorError(shownName, shownCollection))
        }
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            state.setEnabled(name, on)
            return done([on ? ControlTools.turnedOnText(shownName, shownCollection) : ControlTools.turnedOffText(shownName, shownCollection)],
                        ["collection": .string(collection), "name": .string(name), "enabled": .bool(on)],
                        touching: collection, since: mark,
                        restart: on ? ControlTools.restartToUseText : ControlTools.restartToApplyText)
        })
    }

    func switchCollection(_ arguments: ControlArguments) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        // AppState ignores an unknown name in silence, as the chip menu can never offer one;
        // a caller that typed it hears why nothing happened.
        let shown = ControlText.escapedName(collection)
        guard state.store.collections[collection] != nil else { return .refused(MasterStore.noCollectionError(shown)) }
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            state.switchCollection(to: collection)
            return done([ControlTools.switchedText(shown)], ["activeCollection": .string(collection)],
                        touching: nil, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func createCollection(_ arguments: ControlArguments) -> ControlStep {
        let name = arguments.string("name") ?? ""
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.addEmptyCollection(named: name) {
                return ControlTools.refused(error, naming: [name, MasterStore.collectionName(name)])
            }
            let created = MasterStore.collectionName(name)
            return done([ControlTools.createdText(ControlText.escapedName(created))], ["collection": .string(created)],
                        touching: created, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func renameCollection(_ arguments: ControlArguments) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        let newName = arguments.string("newName") ?? ""
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.renameCollection(collection, to: newName) {
                return ControlTools.refused(error, naming: [collection, newName, MasterStore.collectionName(newName)])
            }
            let renamed = MasterStore.collectionName(newName)
            return done([ControlTools.renamedCollectionText(ControlText.escapedName(collection), ControlText.escapedName(renamed))],
                        ["collection": .string(renamed), "previousName": .string(collection)],
                        touching: renamed, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func duplicateCollection(_ arguments: ControlArguments) -> ControlStep {
        let source = arguments.string("collection") ?? ""
        let newName = arguments.string("newName") ?? ""
        let shownSource = ControlText.escapedName(source)
        guard state.store.collections[source] != nil else { return .refused(MasterStore.noCollectionError(shownSource)) }
        // AppState passes over a subscribed source in silence, since the window offers Make Local
        // Copy in its place; that is not a tool here, so the caller is told where to find it.
        guard !state.isSynced(source) else { return .refused(ControlTools.duplicateSubscribedError(shownSource)) }
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.duplicateCollection(source, named: newName) {
                return ControlTools.refused(error, naming: [source, newName, MasterStore.collectionName(newName)])
            }
            let copy = MasterStore.collectionName(newName)
            return done([ControlTools.duplicatedText(shownSource, ControlText.escapedName(copy))],
                        ["collection": .string(copy), "source": .string(source)],
                        touching: copy, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func refreshSubscription(_ arguments: ControlArguments) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        let shown = ControlText.escapedName(collection)
        guard state.store.collections[collection] != nil else { return .refused(MasterStore.noCollectionError(shown)) }
        guard state.isSynced(collection) else { return .refused(ControlTools.notSubscribedError(shown)) }
        return .ready(nil, perform: { [self] in
            state.refreshSource(for: collection)
            let answer = pendingUpdate(collection)
            return ControlOutcome(result: answer, notification: nil)
        })
    }

    func setSetting(_ arguments: ControlArguments, _ caller: ControlCaller) -> ControlStep {
        let key = arguments.string("key") ?? ""
        // Only ASCII space and tab are trimmed, as on Windows.
        let raw = (arguments.string("value") ?? "").trimmingCharacters(in: CharacterSet(charactersIn: " \t"))
        // An unknown key is refused before its value is looked at, so a typo hears which keys exist.
        guard ControlSettingKey.all.contains(key) else {
            return .refused(ControlTools.notOneOfError("key", ControlSettingKey.all.joined(separator: ", ")))
        }
        if caller != .cli, ControlSettingKey.isUserOnly(key) { return .refused(ControlTools.settingNotForClaudeError(key)) }
        if key == ControlSettingKey.backupsToKeep {
            // Digits only: no sign, as on Windows.
            guard !raw.isEmpty, raw.utf8.allSatisfy({ (48...57).contains($0) }), let count = Int(raw),
                  (SettingsModel.minKeepCount...SettingsModel.maxKeepCount).contains(count) else {
                return .refused(ControlTools.keepCountError)
            }
            return .ready(nil, perform: { [self] in
                let mark = state.appliedServers
                settings.backupKeepCount = count
                return done([ControlTools.settingChangedText(key, String(count))], ["key": .string(key), "value": .int(count)],
                            touching: nil, since: mark, restart: ControlTools.restartToApplyText, touchesClaude: false)
            })
        }
        let lowered = raw.lowercased()
        guard ["on", "off", "true", "false"].contains(lowered) else { return .refused(ControlTools.switchValueError(key)) }
        let on = lowered == "on" || lowered == "true"
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            let word = on ? ControlTools.onWord : ControlTools.offWord
            // The integration's own sentence names it as Settings does; every other key reads back
            // as the command line typed it.
            var lines = [key == ControlSettingKey.claudeIntegration
                         ? ControlTools.integrationSetText(word) : ControlTools.settingChangedText(key, word)]
            switch key {
            case ControlSettingKey.launchAtLogin:
                // Read fresh first, as the General tab does: the OS may have changed it since.
                settings.refresh()
                settings.launchAtLogin = on
                guard settings.launchAtLogin == on else { return ControlTools.refused(settings.loginItemNote ?? "") }
                if let note = settings.loginItemNote { lines.append(note) }
            case ControlSettingKey.confirmRestart: settings.confirmBeforeRestart = on
            case ControlSettingKey.confirmQuit: settings.confirmBeforeQuit = on
            case ControlSettingKey.notifyExternalChanges: settings.notifyExternalChanges = on
            case ControlSettingKey.automaticUpdates: settings.autoUpdate = on
            case ControlSettingKey.claudeIntegration:
                if let refusal = state.setClaudeIntegrationEnabled(on) { return ControlTools.refused(refusal) }
            case ControlSettingKey.confirmClaudeChanges: state.setConfirmClaudeChanges(on)
            default: return ControlTools.refused(ControlTools.notOneOfError("key", ControlSettingKey.all.joined(separator: ", ")))
            }
            return done(lines, ["key": .string(key), "value": .bool(on)], touching: nil, since: mark,
                        restart: on ? ControlTools.restartToUseText : ControlTools.restartToApplyText,
                        touchesClaude: key == ControlSettingKey.claudeIntegration)
        })
    }
}
