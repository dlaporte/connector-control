import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTools.Gated.cs
///
/// The writes that change what a connector runs. Each is prepared in full before anyone is asked:
/// every refusal, then the question — who is asking, for what, and exactly what would run, with
/// secrets masked — then the change itself, which runs only on Allow (or with confirmations off,
/// or for the command-line tool) and is announced when nobody was asked.
extension ControlTools {
    public static let takesEffectFooter = "Takes effect when Claude restarts."
    public static let configShapeError = "A connector’s config needs a “command” or a “url”."
    public static let nothingToChangeError = "Give a new config, a new name, or both."

    public static func addTitle(_ asker: String, _ name: String) -> String { "\(asker) wants to add “\(name)”" }

    public static func addActiveMessage(_ collection: String, _ state: String) -> String { "It would join “\(collection)”, the collection Claude is running, switched \(state), and run:" }

    public static func addMessage(_ collection: String, _ state: String) -> String { "It would join “\(collection)”, switched \(state), and run:" }

    public static func changeTitle(_ asker: String, _ name: String) -> String { "\(asker) wants to change “\(name)”" }

    public static func changeActiveMessage(_ collection: String) -> String { "In “\(collection)”, the collection Claude is running:" }

    public static func changeMessage(_ collection: String) -> String { "In “\(collection)”:" }

    public static func deleteTitle(_ asker: String, _ names: String) -> String { "\(asker) wants to delete \(names)" }

    public static func deleteActiveMessage(_ collection: String) -> String { "From “\(collection)”, the collection Claude is running. A copy remains in Backups." }

    public static func deleteMessage(_ collection: String) -> String { "From “\(collection)”. A copy remains in Backups." }

    public static func addedText(_ name: String, _ collection: String, _ state: String) -> String { "Added “\(name)” to “\(collection)”, switched \(state)." }

    public static func needsValuesText(_ names: String) -> String { "It needs a value for \(names): fill it in Connector Control." }

    public static func changedText(_ name: String, _ collection: String) -> String { "Changed “\(name)” in “\(collection)”." }

    public static func renamedText(_ old: String, _ new: String) -> String { "Renamed “\(old)” to “\(new)”." }

    public static func deletedText(_ names: String, _ collection: String) -> String { "Deleted \(names) from “\(collection)”." }

    public static func unchangedError(_ name: String) -> String { "“\(name)” already has that config." }

    public static func addedNotification(_ asker: String, _ name: String, _ collection: String) -> String { "\(asker) added “\(name)” to “\(collection)”." }

    public static func changedNotification(_ asker: String, _ name: String, _ collection: String) -> String { "\(asker) changed “\(name)” in “\(collection)”." }

    public static func deletedNotification(_ asker: String, _ names: String, _ collection: String) -> String { "\(asker) deleted \(names) from “\(collection)”." }

    /// What the editor's JSON view refuses on Save, for a config that arrives as JSON: anything but
    /// an object with a `command` or a `url`, and a canonical mcp-remote line without a valid URL.
    /// The Windows mirror also refuses what the `cmd /c` launcher would hand to cmd.exe; a Mac never
    /// writes that launcher.
    static func configError(_ config: JSONValue) -> String? {
        guard case .object(let object) = config else { return EditorModel.notValidJSON }
        var runnable = false
        if case .string(let command)? = object["command"], !command.trimmingCharacters(in: .whitespaces).isEmpty { runnable = true }
        if case .string(let url)? = object["url"], !url.isEmpty { runnable = true }
        guard runnable else { return configShapeError }
        if RemotePattern.isCanonicalShape(config), RemotePattern.detect(config) == nil { return EditorModel.invalidURLError }
        // The cmd /c launcher hands these fields to cmd.exe, which treats & | < > ^ " and spaces as
        // commands; the Mac never writes that launcher, so its mirror has no such check.
        return nil
    }

    /// The collection a connector tool works in: the one named, else the active one.
    func targetCollection(_ arguments: ControlArguments) -> String {
        arguments.string("collection") ?? state.activeCollection
    }

    func addConnector(_ arguments: ControlArguments, _ asker: String) -> ControlStep {
        let collection = targetCollection(arguments)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(collection)) }
        guard !state.isSynced(collection) else { return .refused(CollectionsModel.addConnectorDisabledTooltip) }
        let name = (arguments.string("name") ?? "").trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return .refused(AppState.nameEmptyError) }
        guard name != ClaudeIntegration.entryName else { return .refused(ClaudeIntegration.reservedNameError) }
        guard held.mcps[name] == nil else { return .refused(AppState.duplicateNameError(name)) }
        let config = arguments.object("config") ?? .object([:])
        if let error = ControlTools.configError(config) { return .refused(error) }
        let on = arguments.bool("enabled") ?? true
        let word = on ? ControlTools.onWord : ControlTools.offWord
        let active = collection == state.activeCollection
        let request = ConfirmationRequest(
            title: ControlTools.addTitle(asker, name),
            message: active ? ControlTools.addActiveMessage(collection, word) : ControlTools.addMessage(collection, word),
            code: ControlMasking.codeLines(config, exposure: .dialog),
            footer: active && on ? ControlTools.takesEffectFooter : nil)
        return .ready(request, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.upsert(name: name, entry: MCPEntry(enabled: on, config: config, lastEditView: .json),
                                        renamedFrom: nil, in: collection) {
                return ControlTools.refused(error)
            }
            state.applyInteractively()
            let needs = Placeholder.unfilledNames(in: config)
            var lines = [ControlTools.addedText(name, collection, word)]
            if !needs.isEmpty { lines.append(ControlTools.needsValuesText(ControlText.quotedList(needs))) }
            return done(lines, ["collection": .string(collection), "name": .string(name), "enabled": .bool(on),
                                "needs": .array(needs.map(JSONValue.string))],
                        touching: collection, since: mark, restart: ControlTools.restartToUseText,
                        notification: ControlTools.addedNotification(asker, name, collection))
        })
    }

    func updateConnector(_ arguments: ControlArguments, _ asker: String) -> ControlStep {
        let collection = targetCollection(arguments)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(collection)) }
        guard !state.isSynced(collection) else { return .refused(CollectionsModel.lockedGlyphTooltip) }
        let name = arguments.string("name") ?? ""
        guard let current = held.mcps[name] else { return .refused(ControlTools.noConnectorError(name, collection)) }
        let config = arguments.object("config")
        let requestedName = arguments.string("newName")
        guard config != nil || requestedName != nil else { return .refused(ControlTools.nothingToChangeError) }
        let newName = requestedName.map { $0.trimmingCharacters(in: .whitespaces) } ?? name
        guard !newName.isEmpty else { return .refused(AppState.nameEmptyError) }
        let renamed = newName != name
        if renamed, newName == ClaudeIntegration.entryName { return .refused(ClaudeIntegration.reservedNameError) }
        if renamed, held.mcps[newName] != nil { return .refused(AppState.duplicateNameError(newName)) }
        if let config, let error = ControlTools.configError(config) { return .refused(error) }
        let after = config ?? current.config
        // "Unchanged" compares the configs themselves, not the rendered lines: every difference is a
        // change, whether or not a line can show it.
        let reconfigured = after != current.config
        guard renamed || reconfigured else { return .refused(ControlTools.unchangedError(name)) }
        let changes = ControlMasking.changeLines(from: current.config, to: after, exposure: .dialog)
        let active = collection == state.activeCollection
        let names = renamed ? [ControlMasking.nameLabel, ControlMasking.removedPrefix + name, ControlMasking.addedPrefix + newName] : []
        let request = ConfirmationRequest(
            title: ControlTools.changeTitle(asker, name),
            message: active ? ControlTools.changeActiveMessage(collection) : ControlTools.changeMessage(collection),
            code: names + changes,
            footer: active && current.enabled ? ControlTools.takesEffectFooter : nil)
        return .ready(request, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.upsert(name: newName,
                                        entry: MCPEntry(enabled: current.enabled, config: after, lastEditView: current.lastEditView),
                                        renamedFrom: name, in: collection) {
                return ControlTools.refused(error)
            }
            state.applyInteractively()
            var lines: [String] = []
            if renamed { lines.append(ControlTools.renamedText(name, newName)) }
            if reconfigured { lines.append(ControlTools.changedText(newName, collection)) }
            return done(lines, ["collection": .string(collection), "name": .string(newName), "previousName": .string(name)],
                        touching: collection, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.changedNotification(asker, newName, collection))
        })
    }

    func deleteConnectors(_ arguments: ControlArguments, _ asker: String) -> ControlStep {
        let collection = targetCollection(arguments)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(collection)) }
        guard !state.isSynced(collection) else { return .refused(CollectionsModel.lockedGlyphTooltip) }
        var names: [String] = []
        for name in arguments.strings("names") ?? [] where !names.contains(name) { names.append(name) }
        guard !names.isEmpty else { return .refused(ControlTools.missingArgumentError("names")) }
        // All or nothing: a list with one wrong name is a mistake, not a request for the rest.
        if let missing = names.first(where: { held.mcps[$0] == nil }) {
            return .refused(ControlTools.noConnectorError(missing, collection))
        }
        let listed = ControlText.quotedList(names)
        let active = collection == state.activeCollection
        let reachesClaude = active && names.contains { held.mcps[$0]?.enabled == true }
        let request = ConfirmationRequest(
            title: ControlTools.deleteTitle(asker, listed),
            message: active ? ControlTools.deleteActiveMessage(collection) : ControlTools.deleteMessage(collection),
            code: [], footer: reachesClaude ? ControlTools.takesEffectFooter : nil)
        return .ready(request, perform: { [self] in
            let mark = state.appliedServers
            state.delete(names: names, in: collection)
            state.applyInteractively()
            return done([ControlTools.deletedText(listed, collection)],
                        ["collection": .string(collection), "names": .array(names.map(JSONValue.string))],
                        touching: collection, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.deletedNotification(asker, listed, collection))
        })
    }
}
