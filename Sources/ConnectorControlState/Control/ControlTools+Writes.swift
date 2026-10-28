import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTools.Writes.cs
///
/// The write tools that change nothing a connector runs, apart from switching one on or off: they
/// never ask, and say what they did and what follows from it.
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
        default: return .refused(ControlTools.unknownToolError(name))
        }
    }

    static func refused(_ text: String) -> ControlOutcome {
        ControlOutcome(result: .refusal(text), notification: nil)
    }

    /// A write's answer: what happened, then what follows for the collection Claude runs. A write
    /// that changed what Claude's config holds (`appliedServers` moved off `mark`, taken before the
    /// write) while Claude runs an older config says to restart; one that could not write it says
    /// the change was saved and gives the app's own message. `collection` nil is a write that
    /// reaches Claude whatever is active. The notification, for a change nobody was asked about,
    /// carries the same restart sentence.
    func done(_ lines: [String], _ fields: [String: JSONValue], touching collection: String?,
              since mark: [String: JSONValue], restart: String, notification: String? = nil) -> ControlOutcome {
        var text = lines
        let reachesClaude = collection == nil || collection == state.activeCollection
        let applied = state.appliedServers != mark
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
        guard state.store.collections[collection]?.mcps[name] != nil else {
            return .refused(ControlTools.noConnectorError(name, collection))
        }
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            state.setEnabled(name, on)
            return done([on ? ControlTools.turnedOnText(name, collection) : ControlTools.turnedOffText(name, collection)],
                        ["collection": .string(collection), "name": .string(name), "enabled": .bool(on)],
                        touching: collection, since: mark,
                        restart: on ? ControlTools.restartToUseText : ControlTools.restartToApplyText)
        })
    }

    func switchCollection(_ arguments: ControlArguments) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        // AppState ignores an unknown name in silence, as the chip menu can never offer one;
        // a caller that typed it hears why nothing happened.
        guard state.store.collections[collection] != nil else { return .refused(MasterStore.noCollectionError(collection)) }
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            state.switchCollection(to: collection)
            return done([ControlTools.switchedText(collection)], ["activeCollection": .string(collection)],
                        touching: nil, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func createCollection(_ arguments: ControlArguments) -> ControlStep {
        let name = arguments.string("name") ?? ""
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.addEmptyCollection(named: name) { return ControlTools.refused(error) }
            let created = MasterStore.collectionName(name)
            return done([ControlTools.createdText(created)], ["collection": .string(created)],
                        touching: created, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func renameCollection(_ arguments: ControlArguments) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        let newName = arguments.string("newName") ?? ""
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.renameCollection(collection, to: newName) { return ControlTools.refused(error) }
            let renamed = MasterStore.collectionName(newName)
            return done([ControlTools.renamedCollectionText(collection, renamed)],
                        ["collection": .string(renamed), "previousName": .string(collection)],
                        touching: renamed, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func duplicateCollection(_ arguments: ControlArguments) -> ControlStep {
        let source = arguments.string("collection") ?? ""
        let newName = arguments.string("newName") ?? ""
        guard state.store.collections[source] != nil else { return .refused(MasterStore.noCollectionError(source)) }
        // AppState passes over a subscribed source in silence, since the window offers Make Local
        // Copy in its place; that is not a tool here, so the caller is told where to find it.
        guard !state.isSynced(source) else { return .refused(ControlTools.duplicateSubscribedError(source)) }
        return .ready(nil, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.duplicateCollection(source, named: newName) { return ControlTools.refused(error) }
            let copy = MasterStore.collectionName(newName)
            return done([ControlTools.duplicatedText(source, copy)], ["collection": .string(copy), "source": .string(source)],
                        touching: copy, since: mark, restart: ControlTools.restartToApplyText)
        })
    }

    func refreshSubscription(_ arguments: ControlArguments) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        guard state.store.collections[collection] != nil else { return .refused(MasterStore.noCollectionError(collection)) }
        guard state.isSynced(collection) else { return .refused(ControlTools.notSubscribedError(collection)) }
        return .ready(nil, perform: { [self] in
            state.refreshSource(for: collection)
            let answer = pendingUpdate(collection)
            return ControlOutcome(result: answer, notification: nil)
        })
    }

    func setSetting(_ arguments: ControlArguments, _ caller: ControlCaller) -> ControlStep {
        let key = arguments.string("key") ?? ""
        let raw = (arguments.string("value") ?? "").trimmingCharacters(in: .whitespaces)
        if caller != .cli, ControlSettingKey.isUserOnly(key) { return .refused(ControlTools.settingNotForClaudeError(key)) }
        if key == ControlSettingKey.backupsToKeep {
            guard let count = Int(raw), (SettingsModel.minKeepCount...SettingsModel.maxKeepCount).contains(count) else {
                return .refused(ControlTools.keepCountError)
            }
            return .ready(nil, perform: { [self] in
                let mark = state.appliedServers
                settings.backupKeepCount = count
                return done([ControlTools.settingChangedText(key, String(count))], ["key": .string(key), "value": .int(count)],
                            touching: nil, since: mark, restart: ControlTools.restartToApplyText)
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
                        restart: on ? ControlTools.restartToUseText : ControlTools.restartToApplyText)
        })
    }
}
