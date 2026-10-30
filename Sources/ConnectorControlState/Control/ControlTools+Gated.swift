import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlTools.Gated.cs
///
/// The writes that change what a connector runs, or what Claude could run: connectors, deleting a
/// collection, importing, subscribing, applying an update, restoring a backup, and the restart.
/// Each is prepared in full before anyone is asked: every refusal, then the question — who is
/// asking, for what, and exactly what would run, with secrets masked — then the change itself,
/// which runs only on Allow (or with confirmations off, or for the command-line tool; never
/// unasked for the restart Claude asks for) and is announced when nobody was asked.
///
/// The question, the answer and the announcement name connectors, collections and the asker as
/// others chose them — Claude names connectors, creates and renames collections without asking, and
/// any client names itself — so every name goes through `ControlText.escapedName(_:)`, and the asker
/// arrives as `ControlCaller.shownAsker`, escaped and framed already: none can break a line, reorder
/// or hide text, or close its own quotes.
extension ControlTools {
    public static let takesEffectFooter = "Takes effect when Claude restarts."
    public static let configShapeError = "A connector’s config needs a “command” or a “url”."
    public static let nothingToChangeError = "Give a new config, a new name, or both."

    public static func addTitle(_ shownAsker: String, _ name: String) -> String { "\(shownAsker) wants to add “\(name)”" }

    public static func addActiveMessage(_ collection: String, _ state: String) -> String { "It would join “\(collection)”, the collection Claude is running, switched \(state), and run:" }

    public static func addMessage(_ collection: String, _ state: String) -> String { "It would join “\(collection)”, switched \(state), and run:" }

    public static func changeTitle(_ shownAsker: String, _ name: String) -> String { "\(shownAsker) wants to change “\(name)”" }

    public static func changeActiveMessage(_ collection: String) -> String { "In “\(collection)”, the collection Claude is running:" }

    public static func changeMessage(_ collection: String) -> String { "In “\(collection)”:" }

    public static func deleteTitle(_ shownAsker: String, _ names: String) -> String { "\(shownAsker) wants to delete \(names)" }

    public static func deleteActiveMessage(_ collection: String) -> String { "From “\(collection)”, the collection Claude is running. A copy remains in Backups." }

    public static func deleteMessage(_ collection: String) -> String { "From “\(collection)”. A copy remains in Backups." }

    public static func addedText(_ name: String, _ collection: String, _ state: String) -> String { "Added “\(name)” to “\(collection)”, switched \(state)." }

    public static func needsValuesText(_ names: String) -> String { "It needs a value for \(names): fill it in Connector Control." }

    public static func changedText(_ name: String, _ collection: String) -> String { "Changed “\(name)” in “\(collection)”." }

    public static func renamedText(_ old: String, _ new: String) -> String { "Renamed “\(old)” to “\(new)”." }

    public static func deletedText(_ names: String, _ collection: String) -> String { "Deleted \(names) from “\(collection)”." }

    public static func unchangedError(_ name: String) -> String { "“\(name)” already has that config." }

    public static func addedNotification(_ shownAsker: String, _ name: String, _ collection: String) -> String { "\(shownAsker) added “\(name)” to “\(collection)”." }

    public static func changedNotification(_ shownAsker: String, _ name: String, _ collection: String) -> String { "\(shownAsker) changed “\(name)” in “\(collection)”." }

    public static func renamedNotification(_ shownAsker: String, _ old: String, _ new: String) -> String { "\(shownAsker) renamed “\(old)” to “\(new)”." }

    public static func deletedNotification(_ shownAsker: String, _ names: String, _ collection: String) -> String { "\(shownAsker) deleted \(names) from “\(collection)”." }

    /// What the editor's JSON view refuses on Save, for a config that arrives as JSON: anything but
    /// an object with a `command` or a `url`, and a canonical mcp-remote line without a valid URL.
    /// The Windows mirror also refuses what the `cmd /c` launcher would hand to cmd.exe; a Mac never
    /// writes that launcher.
    static func configError(_ config: JSONValue) -> String? {
        guard case .object(let object) = config else { return EditorModel.notValidJSON }
        var runnable = false
        if case .string(let command)? = object["command"], !command.trimmingCharacters(in: .whitespaces).isEmpty { runnable = true }
        if case .string(let url)? = object["url"], !url.trimmingCharacters(in: .whitespaces).isEmpty { runnable = true }
        guard runnable else { return configShapeError }
        if RemotePattern.isCanonicalShape(config), RemotePattern.detect(config) == nil { return EditorModel.invalidURLError }
        // Platform idiom: the Windows mirror refuses here what its cmd /c launcher would hand to cmd.exe, which
        // treats & | < > ^ " and spaces as commands; the Mac never writes that launcher, so there is no such check.
        return nil
    }

    // Platform idiom: text is compared scalar by scalar, as the Windows mirror compares it ordinally;
    // Swift's == holds canonically equivalent text equal, which would refuse a change as no change.
    /// Whether two configs are the same, value for value.
    static func identical(_ one: JSONValue, _ other: JSONValue) -> Bool {
        switch (one, other) {
        case (.string(let a), .string(let b)):
            return ControlMasking.same(a, b)
        case (.array(let a), .array(let b)):
            return a.count == b.count && zip(a, b).allSatisfy { identical($0, $1) }
        case (.object(let a), .object(let b)):
            let keys = a.keys.sorted { $0.ordinallyPrecedes($1) }
            let otherKeys = b.keys.sorted { $0.ordinallyPrecedes($1) }
            return keys.count == otherKeys.count && zip(keys, otherKeys).allSatisfy { key, otherKey in
                guard ControlMasking.same(key, otherKey), let value = a[key], let otherValue = b[otherKey] else { return false }
                return identical(value, otherValue)
            }
        default:
            return one == other
        }
    }

    /// The collection a connector tool works in: the one named, else the active one.
    func targetCollection(_ arguments: ControlArguments) -> String {
        arguments.string("collection") ?? state.activeCollection
    }

    func addConnector(_ arguments: ControlArguments, _ shownAsker: String) -> ControlStep {
        let collection = targetCollection(arguments)
        let shownCollection = ControlText.escapedName(collection)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(shownCollection)) }
        guard !state.isSynced(collection) else { return .refused(ControlTools.subscribedReadOnlyError(shownCollection)) }
        let name = (arguments.string("name") ?? "").trimmingCharacters(in: .whitespaces)
        let shownName = ControlText.escapedName(name)
        guard !name.isEmpty else { return .refused(AppState.nameEmptyError) }
        guard name != ClaudeIntegration.entryName else { return .refused(ClaudeIntegration.reservedNameError) }
        guard held.mcps[name] == nil else { return .refused(AppState.duplicateNameError(shownName)) }
        let config = arguments.object("config") ?? .object([:])
        if let error = ControlTools.configError(config) { return .refused(error) }
        let on = arguments.bool("enabled") ?? true
        let word = on ? ControlTools.onWord : ControlTools.offWord
        let active = collection == state.activeCollection
        let request = ConfirmationRequest(
            title: ControlTools.addTitle(shownAsker, shownName),
            message: active ? ControlTools.addActiveMessage(shownCollection, word) : ControlTools.addMessage(shownCollection, word),
            code: ControlMasking.codeLines(config, exposure: .dialog),
            footer: active && on ? ControlTools.takesEffectFooter : nil)
        return .ready(request, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.upsert(name: name, entry: MCPEntry(enabled: on, config: config, lastEditView: .json),
                                        renamedFrom: nil, in: collection) {
                return ControlTools.refused(error, naming: [name, collection])
            }
            state.applyInteractively()
            let needs = Placeholder.unfilledNames(in: config)
            var lines = [ControlTools.addedText(shownName, shownCollection, word)]
            if !needs.isEmpty { lines.append(ControlTools.needsValuesText(ControlText.quotedList(needs))) }
            return done(lines, ["collection": .string(collection), "name": .string(name), "enabled": .bool(on),
                                "needs": .array(needs.map(JSONValue.string))],
                        touching: collection, since: mark, restart: ControlTools.restartToUseText,
                        notification: ControlTools.addedNotification(shownAsker, shownName, shownCollection))
        })
    }

    func updateConnector(_ arguments: ControlArguments, _ shownAsker: String) -> ControlStep {
        let collection = targetCollection(arguments)
        let shownCollection = ControlText.escapedName(collection)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(shownCollection)) }
        guard !state.isSynced(collection) else { return .refused(ControlTools.subscribedReadOnlyError(shownCollection)) }
        let name = arguments.string("name") ?? ""
        let shownName = ControlText.escapedName(name)
        guard let current = held.mcps[name] else { return .refused(ControlTools.noConnectorError(shownName, shownCollection)) }
        let config = arguments.object("config")
        let requestedName = arguments.string("newName")
        guard config != nil || requestedName != nil else { return .refused(ControlTools.nothingToChangeError) }
        let newName = requestedName.map { $0.trimmingCharacters(in: .whitespaces) } ?? name
        let shownNewName = ControlText.escapedName(newName)
        guard !newName.isEmpty else { return .refused(AppState.nameEmptyError) }
        let renamed = newName != name
        if renamed, newName == ClaudeIntegration.entryName { return .refused(ClaudeIntegration.reservedNameError) }
        if renamed, held.mcps[newName] != nil { return .refused(AppState.duplicateNameError(shownNewName)) }
        if let config, let error = ControlTools.configError(config) { return .refused(error) }
        let after = config ?? current.config
        // "Unchanged" compares the configs themselves, not the rendered lines: every difference is a
        // change, whether or not a line can show it.
        let reconfigured = !ControlTools.identical(after, current.config)
        guard renamed || reconfigured else { return .refused(ControlTools.unchangedError(shownName)) }
        let changes = ControlMasking.changeLines(from: current.config, to: after, exposure: .dialog)
        let active = collection == state.activeCollection
        let names = renamed ? [ControlMasking.nameLabel, ControlMasking.removedPrefix + shownName, ControlMasking.addedPrefix + shownNewName] : []
        let request = ConfirmationRequest(
            title: ControlTools.changeTitle(shownAsker, shownName),
            message: active ? ControlTools.changeActiveMessage(shownCollection) : ControlTools.changeMessage(shownCollection),
            code: names + changes,
            footer: active && current.enabled ? ControlTools.takesEffectFooter : nil)
        return .ready(request, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.upsert(name: newName,
                                        entry: MCPEntry(enabled: current.enabled, config: after, lastEditView: current.lastEditView),
                                        renamedFrom: name, in: collection) {
                return ControlTools.refused(error, naming: [newName, name, collection])
            }
            state.applyInteractively()
            var lines: [String] = []
            if renamed { lines.append(ControlTools.renamedText(shownName, shownNewName)) }
            if reconfigured { lines.append(ControlTools.changedText(shownNewName, shownCollection)) }
            return done(lines, ["collection": .string(collection), "name": .string(newName), "previousName": .string(name)],
                        touching: collection, since: mark, restart: ControlTools.restartToApplyText,
                        // A rename and nothing else is announced as one; any other change by what it changed.
                        notification: reconfigured ? ControlTools.changedNotification(shownAsker, shownNewName, shownCollection)
                                                   : ControlTools.renamedNotification(shownAsker, shownName, shownNewName))
        })
    }

    func deleteConnectors(_ arguments: ControlArguments, _ shownAsker: String) -> ControlStep {
        let collection = targetCollection(arguments)
        let shownCollection = ControlText.escapedName(collection)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(shownCollection)) }
        guard !state.isSynced(collection) else { return .refused(ControlTools.subscribedReadOnlyError(shownCollection)) }
        var names: [String] = []
        for name in arguments.strings("names") ?? [] where !names.contains(name) { names.append(name) }
        guard !names.isEmpty else { return .refused(ControlTools.missingArgumentError("names")) }
        // All or nothing: a list with one wrong name is a mistake, not a request for the rest.
        if let missing = names.first(where: { held.mcps[$0] == nil }) {
            return .refused(ControlTools.noConnectorError(ControlText.escapedName(missing), shownCollection))
        }
        let listed = ControlText.quotedList(names)
        let active = collection == state.activeCollection
        let reachesClaude = active && names.contains { held.mcps[$0]?.enabled == true }
        let request = ConfirmationRequest(
            title: ControlTools.deleteTitle(shownAsker, listed),
            message: active ? ControlTools.deleteActiveMessage(shownCollection) : ControlTools.deleteMessage(shownCollection),
            code: [], footer: reachesClaude ? ControlTools.takesEffectFooter : nil)
        return .ready(request, perform: { [self] in
            let mark = state.appliedServers
            state.delete(names: names, in: collection)
            state.applyInteractively()
            return done([ControlTools.deletedText(listed, shownCollection)],
                        ["collection": .string(collection), "names": .array(names.map(JSONValue.string))],
                        touching: collection, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.deletedNotification(shownAsker, listed, shownCollection))
        })
    }

    // MARK: Collections, sharing, backups and restart

    public static let publishedFileStaysText = "Its published file stays in its folder."
    public static let restartItselfTitle = "Claude wants to restart itself"
    public static let restartMessage = "Claude Desktop will quit and reopen to pick up the change. The reply in progress stops; the conversation is kept."
    public static let restartingText = "Restarting Claude Desktop."
    public static let intoOrNewCollectionError = "Give “into” or “newCollection”, not both."

    public static func deleteCollectionTitle(_ shownAsker: String, _ collection: String) -> String { "\(shownAsker) wants to delete the collection “\(collection)”" }

    public static func deletedCollectionText(_ collection: String) -> String { "Deleted the collection “\(collection)”." }

    public static func deletedCollectionNotification(_ shownAsker: String, _ collection: String) -> String { "\(shownAsker) deleted the collection “\(collection)”." }

    public static func importTitle(_ shownAsker: String, _ file: String) -> String { "\(shownAsker) wants to import “\(file)”" }

    public static func importMessage(_ count: Int, _ collection: String) -> String {
        count == 1 ? "\(count) connector would be copied into “\(collection)”, switched off:"
            : "\(count) connectors would be copied into “\(collection)”, switched off:"
    }

    public static func importNewMessage(_ count: Int, _ collection: String) -> String {
        count == 1 ? "\(count) connector would be copied into a new collection, “\(collection)”, switched off:"
            : "\(count) connectors would be copied into a new collection, “\(collection)”, switched off:"
    }

    /// The import question when a Replace lands over a connector that is on: the copy keeps its
    /// switch, so it stays on.
    public static func importReplacingMessage(_ count: Int, _ collection: String) -> String {
        count == 1 ? "\(count) connector would be copied into “\(collection)”, replacing one that is switched on, so it stays on:"
            : "\(count) connectors would be copied into “\(collection)”, switched off, but any that replaces one switched on stays on:"
    }

    /// The file an import or subscription reads, between the app's quotes so the dialog sets it apart;
    /// `path` comes through `ControlText.escapedPath(_:)`.
    public static func fromFooter(_ path: String) -> String { "From “\(path)”" }

    public static func importedText(_ count: Int, _ collection: String) -> String {
        count == 1 ? "Copied \(count) connector into “\(collection)”, switched off."
            : "Copied \(count) connectors into “\(collection)”, switched off."
    }

    /// The import's answer when a Replace landed over a connector that was on: the copy kept its
    /// switch, as the question said it would.
    public static func importedReplacingText(_ count: Int, _ collection: String) -> String {
        count == 1 ? "Copied \(count) connector into “\(collection)”; it replaced one that was switched on, so it stays on."
            : "Copied \(count) connectors into “\(collection)”, switched off, but any that replaced one switched on stays on."
    }

    public static func importedNotification(_ shownAsker: String, _ count: Int, _ collection: String) -> String {
        count == 1 ? "\(shownAsker) copied \(count) connector into “\(collection)”."
            : "\(shownAsker) copied \(count) connectors into “\(collection)”."
    }

    public static func nothingToImportError(_ file: String) -> String { "Nothing in “\(file)” would come across." }

    public static func notAFileError(_ file: String) -> String { "“\(file)” isn’t a file." }

    public static func documentTooLargeError(_ file: String) -> String { "“\(file)” is over 1 MB, too large for a collection file." }

    public static func subscribeTitle(_ shownAsker: String, _ file: String) -> String { "\(shownAsker) wants to subscribe to “\(file)”" }

    public static func subscribeMessage(_ collection: String, _ count: Int) -> String {
        count == 1 ? "A new collection, “\(collection)”, would follow this file, read-only. Its \(count) connector arrives switched off:"
            : "A new collection, “\(collection)”, would follow this file, read-only. Its \(count) connectors arrive switched off:"
    }

    public static func subscribedText(_ collection: String, _ file: String) -> String { "Subscribed: “\(collection)” follows \(file), read-only." }

    public static func arrivedOffText(_ count: Int) -> String {
        count == 1 ? "\(count) connector arrived switched off." : "\(count) connectors arrived switched off."
    }

    public static func subscribedNotification(_ shownAsker: String, _ file: String, _ collection: String) -> String { "\(shownAsker) subscribed to “\(file)” as “\(collection)”." }

    public static func applyUpdateTitle(_ shownAsker: String, _ collection: String) -> String { "\(shownAsker) wants to update “\(collection)”" }

    public static func appliedUpdateText(_ collection: String) -> String { "Applied the update to “\(collection)”." }

    public static func appliedUpdateNotification(_ shownAsker: String, _ collection: String) -> String { "\(shownAsker) applied the update to “\(collection)”." }

    public static func noUpdateError(_ collection: String) -> String { "“\(collection)” has no update waiting." }

    public static func restoreTitle(_ shownAsker: String, _ id: String) -> String { "\(shownAsker) wants to restore “\(id)”" }

    public static func restoreIntoText(_ collection: String) -> String { "Its connectors go back into “\(collection)”:" }

    public static func restoreActivatingText(_ collection: String) -> String { "Its connectors go back into “\(collection)”, which becomes the active collection:" }

    public static func restoredText(_ id: String) -> String { "Restored “\(id)”." }

    public static func restoredNotification(_ shownAsker: String, _ id: String) -> String { "\(shownAsker) restored “\(id)”." }

    public static func noBackupError(_ id: String) -> String { "There’s no backup named “\(id)”." }

    public static func restartTitle(_ shownAsker: String) -> String { "\(shownAsker) wants to restart Claude Desktop" }

    /// A connector as a question lists it: its name in quotes, through `ControlText.quotedList(_:)`
    /// — so no name can close its quotes, break its line, or pass for a line of what a connector
    /// runs, which are indented under it — then what becomes of it, when that is said.
    static func connectorHeading(_ name: String, _ detail: String? = nil) -> String {
        let quoted = ControlText.quotedList([name])
        return detail.map { ControlTools.detail(quoted, ControlText.escaped($0)) } ?? quoted
    }

    /// What a line under a connector's heading starts with.
    static let indent = "  "

    /// Lines under a connector's heading, indented so they read as its own.
    static func indented(_ lines: [String]) -> [String] {
        lines.map { indent + $0 }
    }

    /// A document's connectors as the import and subscribe questions list them, in the Import
    /// sheet's order and words: each by name with what it would run, as the dialog shows a config
    /// (`${COLLECTION_DIR}` as the document writes it, the footer naming the folder it stands for);
    /// a name the collection already holds with what happens to it; one this platform cannot carry
    /// with why.
    static func documentLines(_ rendered: RenderedCollection, present: (String) -> Bool, choice: ImportChoice) -> [String] {
        var lines: [String] = []
        for name in Set(rendered.connectors.keys).union(rendered.excluded.keys).sorted(by: { $0.ordinallyPrecedes($1) }) {
            if let reason = rendered.excluded[name] {
                lines.append(connectorHeading(name, ImportModel.skippedBadge(reason)))
                continue
            }
            guard let connector = rendered.connectors[name] else { continue }
            let runs = indented(ControlMasking.codeLines(connector.config, exposure: .dialog))
            guard present(name) else {
                lines += [connectorHeading(name)] + runs
                continue
            }
            switch choice {
            case .replace: lines += [connectorHeading(name, ImportModel.replaceTitle)] + runs
            case .keepBoth: lines += [connectorHeading(name, ImportModel.keepBothTitle)] + runs
            case .skip, .add: lines.append(connectorHeading(name, ImportModel.presentBadge))
            }
        }
        return lines
    }

    /// The collection's own Delete: the window's question, refused first for what the window
    /// refuses. A subscribed collection's file is never touched, nor a published one's — the window
    /// asks whether that one goes too, and nothing here asks twice, so it stays, as Keep leaves it.
    func deleteCollection(_ arguments: ControlArguments, _ shownAsker: String) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        let shown = ControlText.escapedName(collection)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(shown)) }
        let synced = state.isSynced(collection)
        // AppState's own rule, asked before the question rather than after it.
        if !synced, state.localCollectionNames.count <= 1 { return .refused(AppState.lastLocalCollectionError) }
        let active = collection == state.activeCollection
        let count = held.mcps.count
        // The window's Delete confirmation, sentence for sentence and in its order.
        var sentences = [CollectionsModel.deleteConnectorsSentence(count)]
        if count > 0 { sentences.append(CollectionsModel.deleteCopiesSentence) }
        if active, let next = state.activeAfterDeleting(collection) {
            sentences.append(CollectionsModel.deleteNextActiveSentence(ControlText.escapedName(next)))
        }
        if synced { sentences.append(CollectionsModel.deleteSourceSentence) }
        if state.collectionsCache.published[collection] != nil, state.isPublished(collection) {
            sentences.append(ControlTools.publishedFileStaysText)
        }
        if count > 0 { sentences.append(CollectionsModel.deleteCheckedInformative) }
        let request = ConfirmationRequest(title: ControlTools.deleteCollectionTitle(shownAsker, shown),
                                          message: sentences.joined(separator: " "), code: [],
                                          footer: active ? ControlTools.takesEffectFooter : nil)
        return .ready(request, perform: { [self] in
            let mark = state.appliedServers
            if let error = state.deleteCollection(named: collection) { return ControlTools.refused(error, naming: [collection]) }
            return done([ControlTools.deletedCollectionText(shown)],
                        ["collection": .string(collection), "activeCollection": .string(state.activeCollection)],
                        // Only deleting the collection Claude runs reaches its config; deleting
                        // another must not report a retry an earlier write left pending.
                        touching: active ? nil : collection, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.deletedCollectionNotification(shownAsker, shown))
        })
    }

    /// Why the document at `url` is not read, or nil to read it, found with a stat before anything
    /// opens it, so no path can stall the app or reach what is not a file: it must be a regular
    /// file, or a link to one — not a folder, a FIFO, a device, or a link to one — of at most
    /// `McpSession.maxMessageBytes`, the most a message carries. A path with nothing there is left
    /// to the read, which says so in the system's words.
    static func documentFileRefusal(_ url: URL) -> String? {
        // Platform idiom: stat(2), which follows a link and reads every file type; the Windows mirror
        // reads FileInfo, which knows a folder from a file and no more, and refuses a device by its
        // \\.\ form.
        var status = stat()
        guard stat(url.path, &status) == 0 else { return nil }
        let file = ControlText.escapedName(url.lastPathComponent)
        guard status.st_mode & S_IFMT == S_IFREG else { return notAFileError(file) }
        return status.st_size > Int64(McpSession.maxMessageBytes) ? documentTooLargeError(file) : nil
    }

    func importDocument(_ arguments: ControlArguments, _ caller: ControlCaller) -> ControlStep {
        let path = arguments.string("path") ?? ""
        // An empty path names the working folder here and is no path at all on Windows: refused
        // alike on both.
        guard !path.isEmpty else { return .refused(ControlTools.missingArgumentError("path")) }
        // One place or the other, as the command line's --into | --new.
        guard arguments.string("into") == nil || arguments.string("newCollection") == nil else {
            return .refused(ControlTools.intoOrNewCollectionError)
        }
        // Platform idiom: a URL takes any text, and the read below refuses what names no file; the
        // Windows mirror first refuses a path .NET cannot make full.
        let url = URL(fileURLWithPath: path).standardizedFileURL
        // Platform idiom: the Windows mirror first refuses, by its form, a path that reaches another computer
        // or a device: the relay any that starts with \\ or \??\ once made full — opening one has Windows
        // sign in to that host as the user, with nothing asked — and every caller a \\.\ path or a
        // \\?\ or \??\ path to anything but a drive or a share. A Mac path has no such form. A path under
        // /Volumes is a share the person mounted themselves; one under /net is mounted by autofs on first
        // access, but as an NFS mount that sends none of the person's credentials, so neither signs in
        // anywhere on their behalf. A device is refused below as not a regular file.
        if let refusal = ControlTools.documentFileRefusal(url) { return .refused(refusal) }
        let file = ControlText.escapedName(url.lastPathComponent)
        let (read, bytes, failure) = AppState.readDocument(at: url)
        guard let document = read, let bytes else {
            return .refused(ControlTools.shown(failure ?? EditorModel.notValidJSON, leadingWith: url.lastPathComponent))
        }
        let newCollection = arguments.string("newCollection").map(MasterStore.collectionName)
        let target: String
        if let newCollection {
            // The store's own refusal for a name it cannot take, found on a copy so nothing is made.
            var probe = state.store
            if let error = probe.addCollection(named: newCollection) { return .refused(ControlText.escaped(error, naming: [newCollection])) }
            target = newCollection
        } else {
            // The Import sheet's own target: the active collection while it is local, else the
            // first local one.
            let locals = state.localCollectionNames
            target = arguments.string("into")
                ?? (locals.contains(state.activeCollection) ? state.activeCollection : (locals.first ?? state.activeCollection))
            guard state.store.collections[target] != nil else { return .refused(MasterStore.noCollectionError(ControlText.escapedName(target))) }
            guard !state.isSynced(target) else { return .refused(AppState.targetMustBeLocalError) }
        }
        let shownTarget = ControlText.escapedName(target)
        let clash: ImportChoice
        switch arguments.string("onClash") {
        case "replace": clash = .replace
        case "keep-both": clash = .keepBoth
        default: clash = .skip
        }
        let rendered = document.render()
        // A new collection holds nothing yet, whatever an existing one of that name might.
        let held = newCollection == nil ? (state.store.collections[target]?.mcps ?? [:]) : [:]
        var choices: [String: ImportChoice] = [:]
        for name in rendered.connectors.keys { choices[name] = held[name] == nil ? .add : clash }
        let count = choices.values.filter { $0 != .skip }.count
        guard count > 0 else { return .refused(ControlTools.nothingToImportError(file)) }
        let shownAsker = caller.shownAsker
        // A Replace keeps the switch of what it replaces, as the Import sheet's does: over a connector
        // that is on, the copy stays on and, in the collection Claude runs, changes what Claude runs.
        let replacesOn = choices.contains { $0.value == .replace && held[$0.key]?.enabled == true }
        let from = ControlTools.fromFooter(ControlText.escapedPath(url.path))
        let message = newCollection != nil ? ControlTools.importNewMessage(count, shownTarget)
            : replacesOn ? ControlTools.importReplacingMessage(count, shownTarget) : ControlTools.importMessage(count, shownTarget)
        let request = ConfirmationRequest(
            title: ControlTools.importTitle(shownAsker, file),
            message: message,
            code: ControlTools.documentLines(rendered, present: { held[$0] != nil }, choice: clash),
            footer: replacesOn && target == state.activeCollection ? ControlTools.takesEffectFooter + " " + from : from)
        // What the question cannot show, the document's masked values among them, the bytes stand for.
        return .ready(request, fingerprint: ContentHash.sha256(bytes), perform: { [self] in
            let mark = state.appliedServers
            if let newCollection, let error = state.addEmptyCollection(named: newCollection) {
                return ControlTools.refused(error, naming: [newCollection])
            }
            if let error = state.importCopies(document: document, at: url, into: target, choices: choices) {
                return ControlOutcome(result: .refusal(ControlTools.shown(error, leadingWith: url.lastPathComponent, naming: [target])), notification: nil)
            }
            let answer = replacesOn ? ControlTools.importedReplacingText(count, shownTarget)
                : ControlTools.importedText(count, shownTarget)
            return done([answer], ["collection": .string(target), "count": .int(count)],
                        touching: target, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.importedNotification(shownAsker, count, shownTarget))
        })
    }

    func subscribeDocument(_ arguments: ControlArguments, _ caller: ControlCaller) -> ControlStep {
        let path = arguments.string("path") ?? ""
        // As import_document refuses it.
        guard !path.isEmpty else { return .refused(ControlTools.missingArgumentError("path")) }
        // Platform idiom: as import_document reads it.
        let url = URL(fileURLWithPath: path).standardizedFileURL
        // As import_document checks it.
        if let refusal = ControlTools.documentFileRefusal(url) { return .refused(refusal) }
        let file = ControlText.escapedName(url.lastPathComponent)
        let (read, bytes, failure) = AppState.readDocument(at: url)
        guard let document = read, let bytes else {
            return .refused(ControlTools.shown(failure ?? EditorModel.notValidJSON, leadingWith: url.lastPathComponent))
        }
        // AppState's own rules, asked here as well, so the refusal comes before the question.
        if let origin = document.origin, state.collectionsFile.collections.values.contains(where: { $0.publish?.origin == origin }) {
            return .refused(AppState.ownCollectionError)
        }
        let requested = MasterStore.collectionName(arguments.string("name") ?? "")
        let name = MasterStore.collectionName(requested.isEmpty ? document.name : requested)
        var probe = state.store
        if let error = probe.addCollection(named: name) { return .refused(ControlText.escaped(error, naming: [name])) }
        let shownName = ControlText.escapedName(name)
        let rendered = document.render()
        let count = rendered.connectors.count
        let shownAsker = caller.shownAsker
        let request = ConfirmationRequest(
            title: ControlTools.subscribeTitle(shownAsker, file),
            message: ControlTools.subscribeMessage(shownName, count),
            code: ControlTools.documentLines(rendered, present: { _ in false }, choice: .add),
            footer: ControlTools.fromFooter(ControlText.escapedPath(url.path)))
        // As import_document's: the bytes stand for what the question cannot show.
        return .ready(request, fingerprint: ContentHash.sha256(bytes), perform: { [self] in
            let mark = state.appliedServers
            if let error = state.subscribe(document: document, data: bytes, at: url, as: arguments.string("name")) {
                return ControlOutcome(result: .refusal(ControlTools.shown(error, leadingWith: url.lastPathComponent, naming: [name])), notification: nil)
            }
            return done([ControlTools.subscribedText(shownName, file), ControlTools.arrivedOffText(count)],
                        ["collection": .string(name), "count": .int(count)],
                        touching: name, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.subscribedNotification(shownAsker, file, shownName))
        })
    }

    func applyUpdate(_ arguments: ControlArguments, _ shownAsker: String) -> ControlStep {
        let collection = arguments.string("collection") ?? ""
        let shown = ControlText.escapedName(collection)
        guard let held = state.store.collections[collection] else { return .refused(MasterStore.noCollectionError(shown)) }
        guard state.isSynced(collection) else { return .refused(ControlTools.notSubscribedError(shown)) }
        if let problem = state.sourceErrors[collection] {
            // The problem names the document's file.
            let file = state.sourceBinding(of: collection)?.path.map { URL(fileURLWithPath: $0).lastPathComponent }
            return .refused(file.map { ControlTools.shown(problem, leadingWith: $0) } ?? ControlText.escaped(problem))
        }
        guard let diff = state.pendingUpdates[collection] else { return .refused(ControlTools.noUpdateError(shown)) }
        // Connector by connector, as the Review sheet shows an update: every entry is a command
        // Claude will run. A document that moves under the question then asks a different one, so
        // an Allow cannot land what nobody read.
        let incoming = state.pendingDocument(for: collection)?.connectors ?? [:]
        var code: [String] = []
        for name in diff.added {
            code.append(ControlTools.connectorHeading(name, ReviewModel.addedLabel))
            if let after = incoming[name]?.config { code += ControlTools.indented(ControlMasking.codeLines(after, exposure: .dialog)) }
        }
        for name in diff.removed { code.append(ControlTools.connectorHeading(name, ReviewModel.deletedLabel)) }
        for name in diff.changed {
            code.append(ControlTools.connectorHeading(name, ReviewModel.changedLabel))
            if let before = held.mcps[name]?.config, let after = incoming[name]?.config {
                code += ControlTools.indented(ControlMasking.changeLines(from: before, to: after, exposure: .dialog))
            }
        }
        let active = collection == state.activeCollection
        let request = ConfirmationRequest(title: ControlTools.applyUpdateTitle(shownAsker, shown),
                                          message: ControlTools.pendingUpdateText(shown, ControlTools.changesText(diff)),
                                          code: code, footer: active ? ControlTools.takesEffectFooter : nil)
        // The document's bytes, as the Review sheet's Apply checks them: what the question cannot
        // show, its masked values among them, must not have moved either.
        return .ready(request, fingerprint: state.pendingSourceHash(for: collection), perform: { [self] in
            let mark = state.appliedServers
            if let error = state.applyPendingUpdate(for: collection) { return ControlTools.refused(error, naming: [collection]) }
            return done([ControlTools.appliedUpdateText(shown)], ["collection": .string(collection)],
                        touching: collection, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.appliedUpdateNotification(shownAsker, shown))
        })
    }

    /// A refused restore in AppState's own sentences, the collection each names through
    /// `ControlText.escapedName(_:)`: Claude names collections.
    static func restoreRefusalText(_ refusal: RestoreError) -> String {
        switch refusal {
        case .collectionGone(let collection): return AppState.restoreCollectionGoneError(ControlText.escapedName(collection))
        case .subscribedActive(let collection): return AppState.restoreSubscribedError(ControlText.escapedName(collection))
        }
    }

    /// The Restore sheet's restore, by an id `list_backups` gave, refused first for what the sheet
    /// refuses; the question says which connectors come back, and into which collection.
    func restoreBackup(_ arguments: ControlArguments, _ shownAsker: String) -> ControlStep {
        let id = arguments.string("id") ?? ""
        let shownID = ControlText.escapedName(id)
        let urls: [URL]
        do {
            urls = try backupURLs()
        } catch {
            return .refused(ControlText.escaped(AppState.friendly(error)))
        }
        // Only an id the list gives: never a path, so nothing outside the backups folder is read.
        // Compared scalar by scalar, as the Windows mirror compares ordinally.
        guard let backup = urls.first(where: { ControlMasking.same($0.lastPathComponent, id) }) else {
            return .refused(ControlTools.noBackupError(shownID))
        }
        // The backup's record, read once: the refusal, the question and the restore all follow it.
        let recorded = BackupCollections.collection(of: backup, in: state.service.paths.backupsDirURL)
        if let refusal = state.restoreRefusal(forRecorded: recorded) { return .refused(ControlTools.restoreRefusalText(refusal)) }
        // A backup that is not a config file is refused now rather than after the question, in the
        // reader's own words, as the Restore sheet shows a failed restore's. Its bytes stand for what
        // the question, which lists only names, cannot show.
        // Read once: the question, the fingerprint and the restore all come from these bytes.
        let servers: [String: JSONValue]
        let bytes: Data
        do {
            bytes = try BoundedRead.data(at: backup, limit: ConfigService.maxBackupBytes)
            servers = try ClaudeConfigIO.mcpServers(in: bytes)
            try ClaudeConfigIO.requireUniqueKeys(in: bytes)
        } catch {
            // Platform idiom: every error the read throws is the backup's; a programming fault traps rather
            // than throws, so this needs none of the filter the Windows mirror's catch has.
            return .refused(ControlText.escaped(error.localizedDescription, naming: [id]))
        }
        let fingerprint = ContentHash.sha256(bytes)
        // AppState's rule: back into the collection the backup was taken from, else the active one.
        let target = recorded ?? state.activeCollection
        let shownTarget = ControlText.escapedName(target)
        let into = target == state.activeCollection ? ControlTools.restoreIntoText(shownTarget) : ControlTools.restoreActivatingText(shownTarget)
        // Connector Control's own entry is the app's, not a connector the backup brings back.
        let names = servers.keys.filter { $0 != ClaudeIntegration.entryName }.sorted { $0.ordinallyPrecedes($1) }
        let request = ConfirmationRequest(title: ControlTools.restoreTitle(shownAsker, shownID),
                                          message: RestoreModel.caption + " " + into,
                                          code: names.map { ControlTools.connectorHeading($0) },
                                          footer: ControlTools.takesEffectFooter)
        return .ready(request, fingerprint: fingerprint, perform: { [self] in
            let mark = state.appliedServers
            do {
                try state.restoreClaudeConfig(from: backup, data: bytes, recorded: recorded)
            } catch let refusal as RestoreError {
                return ControlOutcome(result: .refusal(ControlTools.restoreRefusalText(refusal)), notification: nil)
            } catch {
                // Platform idiom: every other error lands here, as the Restore sheet's catch takes it;
                // the Windows mirror catches the sheet's own set (a config, I/O, access or JSON error)
                // and leaves anything else to the write queue, which ends the call.
                // A restore that failed is the banner's too, as the Restore sheet's is, in the
                // error's own words.
                state.lastError = error.localizedDescription
                return ControlTools.refused(error.localizedDescription, naming: [id])
            }
            return done([ControlTools.restoredText(shownID)], ["id": .string(id), "activeCollection": .string(state.activeCollection)],
                        touching: nil, since: mark, restart: ControlTools.restartToApplyText,
                        notification: ControlTools.restoredNotification(shownAsker, shownID))
        })
    }

    /// Always asked about when Claude calls it, whatever the confirmation setting; never asked about
    /// from the command line, where the typed command is the confirmation, as the notification's
    /// Restart button is. Either way it is `performRestartClaude()`, not the in-app button's
    /// `restartClaude()`: the gate has asked already, or need not, and the app's own "confirm
    /// before restarting" question would ask a second time.
    func restartClaude(_ shownAsker: String) -> ControlStep {
        let request = ConfirmationRequest(
            title: shownAsker == ControlCaller.claudeName ? ControlTools.restartItselfTitle : ControlTools.restartTitle(shownAsker),
            message: ControlTools.restartMessage, code: [], footer: nil)
        return .ready(request, perform: { [self] in
            // Claude hears the answer first: the restart quits it, and the relay with it. One still
            // due when the app quits never happens. It reports through AppState's own banner when it
            // completes; the Windows mirror starts it fire-and-forget, as the toast's Restart button
            // does.
            host.delay(ControlTools.restartDelay) { [weak self] in
                guard let self, !self.state.isDisposed else { return }
                self.state.performRestartClaude()
            }
            return ControlOutcome(result: ControlResult(structured: .object(["restarting": .bool(true)]),
                                                        text: ControlTools.restartingText, isError: false),
                                  notification: nil)
        })
    }
}
