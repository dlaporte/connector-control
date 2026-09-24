import Combine
import Foundation
import ConnectorControlCore

/// The Collections window, minus pixels: the collections as items in the left pane, the selected
/// collection's connectors as rows in the right one, the detail line above them, and the controls
/// that follow the selection: the sidebar's +, the header's ⋯ menu, the list header's + and the
/// selection bar. Everything is derived from AppState; the model owns only what the window itself
/// knows — which collection is showing and which rows are ticked.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionsModel.cs
@MainActor
public final class CollectionsModel: ObservableObject {
    public static let windowTitle = "Collections"
    public static let importButton = "Import"
    public static let subscribeButton = "Subscribe"
    public static let publishButton = "Start Publishing"
    /// The same sheet, reached from a collection that already publishes.
    public static let publishSettingsButton = "Publishing Settings"
    public static let refreshButton = "Refresh"
    public static let makeLocalCopyButton = "Make Local Copy"
    public static let newButton = "New Collection"
    public static let renameAction = "Rename"
    public static let deleteAction = "Delete"
    public static let stopPublishingAction = "Stop Publishing"
    public static let stopSyncingAction = "Stop Syncing"
    public static let stopSyncingInformative = "The connectors stay as a local collection you can edit."
    public static func stopSyncingMessage(_ collection: String) -> String { "Stop Syncing “\(collection)”?" }
    public static let activeSuffix = " · active"
    public static let unlocatedDetail = "synced · file not located on this machine"
    /// The source's status in the detail line. The cache records no timestamp, so this says what
    /// is true of the file, not how long ago it last changed.
    public static let updateAvailableStatus = "update available"
    public static let upToDateStatus = "up to date"
    /// The two answers to the published-document question. Keep is the default: a file the team
    /// reads is not something to remove by pressing Return.
    public static let removeFileButton = "Remove"
    public static let keepFileButton = "Keep"
    public static let remoteType = "remote"
    /// The sidebar's double-click, and the same action in its context menu.
    public static let makeActiveAction = "Make Active"
    /// The lock at the head of a synced collection's row, and on the popover's rows too, which
    /// is why it is `nonisolated`: `ConnectorRow` is a plain value that reads it off the main
    /// actor, the same reason `AppState.chooseClaude` is spelled that way.
    nonisolated public static let lockedGlyphTooltip = "Read-only: synced from the collection’s author"

    // MARK: - Selection bar

    public static let copyToButton = "Copy to"
    public static let exportCheckedButton = "Export"
    public static let removeCheckedButton = "Remove"

    /// The bar's own tally, e.g. "2 selected".
    public static func selectedCount(_ n: Int) -> String { "\(n) selected" }

    /// Names the connector when there is exactly one ticked, and states the count otherwise: a
    /// removal of one row deserves the same specificity the editor's own Remove used to give it,
    /// and a removal of several would only get longer for naming them all.
    public static func removeCheckedMessage(_ names: [String]) -> String {
        names.count == 1 ? "Remove “\(names[0])”?" : "Remove \(names.count) connectors?"
    }

    /// Lifted from the editor's Remove confirmation, which the list now replaces: the sentence —
    /// the most useful thing in that confirmation — survives here unchanged.
    public static let removeCheckedInformative = "A copy remains in Backups."

    /// The sidebar's chain glyph, or nil when there is no chain to explain: a local collection
    /// has no source, and a synced one whose file is still to be found has no path to name. The
    /// sentence is the popover chip's, borrowed rather than copied — one fact, one wording.
    ///
    /// Takes the item rather than the path so that "which items have a tooltip" stays here; a
    /// view mapping over an optional path would be the same rule, kept somewhere worse.
    public static func syncedGlyphTooltip(_ item: Item) -> String? {
        guard let source = item.source else { return nil }
        return PopoverModel.sourceTooltipFormat(source)
    }

    public static func localDetail(_ count: Int) -> String { "local · \(count) connectors" }

    /// `source` is the document's path on this machine, never the sidecar's origin, which is a UUID.
    public static func syncedDetail(_ source: String, _ status: String) -> String { "synced from \(source) · read-only · \(status)" }

    /// "this Mac" is the platform-forced half of this sentence; the Windows mirror says "this PC".
    public static func publishedDetail(_ folder: String) -> String { "publishes to \(folder) from this Mac" }

    public static func deletePublishedFileQuestion(_ fileName: String) -> String { "Also remove \(fileName) from the folder?" }

    /// One collection in the left pane. A published collection carries no mark of its own there:
    /// publishing is what the detail line and the header's pill say, not a sidebar glyph.
    public struct Item: Identifiable, Equatable, Sendable {
        public let id: String
        public let name: String
        public let kind: CollectionKind
        public let isActive: Bool
        public let hasPendingUpdate: Bool
        public let isLocated: Bool
        /// Where a synced collection's document is, as far as this machine knows: the path it is
        /// bound to, or the name the sidecar recorded while the file is still to be found. nil
        /// for a local collection, which has no source, and for a synced one the sidecar never
        /// named. `AppState.sourceLocation(of:)` is the rule, shared with the popover's chip and
        /// menu, so the sidebar's chain and the chip cannot name the same collection differently.
        public let source: String?

        public init(name: String, kind: CollectionKind, isActive: Bool,
                    hasPendingUpdate: Bool, isLocated: Bool, source: String? = nil) {
            self.id = name
            self.name = name
            self.kind = kind
            self.isActive = isActive
            self.hasPendingUpdate = hasPendingUpdate
            self.isLocated = isLocated
            self.source = source
        }
    }

    /// One entry in Copy to's destination menu. Every collection except the one the rows are in
    /// appears; a synced one is listed but cannot take copies, so the menu can say why rather than
    /// hide it.
    public struct CopyDestination: Equatable, Identifiable, Sendable {
        public let name: String
        /// False for a synced collection: its connectors are the author's.
        public let isEnabled: Bool
        public var id: String { name }

        public init(name: String, isEnabled: Bool) {
            self.name = name
            self.isEnabled = isEnabled
        }
    }

    /// The menu's annotation beside a disabled destination — why a synced collection is listed
    /// but cannot be chosen.
    public static let readOnlyNote = "read-only"

    /// One connector of the selected collection. `checked` is the window's own state — an export
    /// tick, not anything the store holds — so it is the one field the model fills in itself.
    public struct Row: Identifiable, Equatable, Sendable {
        public let id: String
        public let name: String
        public let caution: String?
        public let isLocked: Bool
        public var checked: Bool
        public let target: String

        public init(name: String, caution: String?, isLocked: Bool, checked: Bool, target: String) {
            self.id = name
            self.name = name
            self.caution = caution
            self.isLocked = isLocked
            self.checked = checked
            self.target = target
        }
    }

    /// What the last action AppState refused reported, cleared by the next one that succeeds, is
    /// cancelled, declined at its confirmation, or finds nothing to do.
    @Published public private(set) var lastError: String?

    private let state: AppState
    private let dialogs: Dialogs
    private var subscriptions: Set<AnyCancellable> = []
    /// What the view last picked, which may name a collection that no longer exists; `selected`
    /// resolves it. Stored rather than published because a copy of AppState's answers kept here
    /// would be one change behind: `@Published` fires before the value it announces is in place.
    private var selection: String?
    private var checkedNames_: Set<String> = []
    /// Which collection the ticks above belong to. The window shows one collection at a time, and
    /// a tick must not survive into another one that happens to hold a connector of that name.
    private var checkedCollection: String?

    public init(state: AppState, dialogs: Dialogs) {
        self.state = state
        self.dialogs = dialogs
        // Everything this window reads: the store behind the items and rows, the sidecar and the
        // cache behind their marks, the two derived maps behind the detail line's status, and
        // the last failed publish behind the banner strip.
        relay(state.$store)
        relay(state.$collectionsFile)
        relay(state.$collectionsCache)
        relay(state.$pendingUpdates)
        relay(state.$sourceErrors)
        relay(state.$publishError)
        // A remembered name the store no longer holds is forgotten as soon as it stops resolving,
        // not only when a view happens to write its selection back. The new store comes from the
        // publisher rather than from `state`: `@Published` announces a change before the property
        // holds it.
        state.$store.dropFirst()
            .sink { [weak self] store in self?.forgetUnresolvedSelection(in: store) }
            .store(in: &subscriptions)
    }

    private func relay<P: Publisher>(_ publisher: P) where P.Failure == Never {
        publisher.dropFirst()
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &subscriptions)
    }

    // MARK: - Selection

    /// The collection the right pane is showing. It defaults to the active one and falls back to
    /// it whenever the chosen name stops being a collection — deleted here, or renamed from
    /// anywhere else.
    ///
    /// An assignment that would show the collection already showing does nothing at all — no
    /// republish, no cleared ticks, not even the name remembered. A view that writes its selection
    /// straight back would otherwise feed itself, and remembering the name would pin the window
    /// to a collection it was only showing because it was the active one.
    public var selected: String? {
        get { selectedCollection }
        set {
            guard effectiveCollection(for: newValue) != selectedCollection else {
                forgetUnresolvedSelection(in: state.store)
                return
            }
            objectWillChange.send()
            selection = newValue
            checkedNames_ = []
            checkedCollection = nil
        }
    }

    private var selectedCollection: String { effectiveCollection(for: selection) }

    /// Lets go of a remembered name that no longer names a collection. Nothing on screen changes —
    /// the window was already showing the fallback — so nothing is republished. Without it, a
    /// collection renamed away and later renamed back would pull the window to it unprompted,
    /// because the name would start resolving again.
    private func forgetUnresolvedSelection(in store: MasterStore) {
        if let remembered = selection, store.collections[remembered] == nil { selection = nil }
    }

    /// What a chosen name resolves to: itself while it is a collection, the active one otherwise.
    private func effectiveCollection(for chosen: String?) -> String {
        if let chosen, state.store.collections[chosen] != nil { return chosen }
        return state.activeCollection
    }

    /// The ticks, but only while the collection they were made in is still the one showing.
    private var activeChecks: Set<String> {
        checkedCollection == selectedCollection ? checkedNames_ : []
    }

    /// Moves the window to a collection this model just created or renamed, keeping the ticks
    /// with it — unlike `selected`, which is the user picking a different collection.
    private func retarget(to name: String) {
        objectWillChange.send()
        if checkedCollection != nil { checkedCollection = name }
        selection = name
    }

    // MARK: - Panes

    /// Rebuilt on every read, which is fine here: SwiftUI's List diffs its rows by id and keeps an
    /// unchanged row's view, focus included. The Windows mirror keeps both lists and replaces one
    /// only when its content changes, because WPF regenerates every container on a new list.
    public var items: [Item] {
        let active = state.activeCollection
        return state.collectionNames.map { name in
            Item(name: name, kind: state.kind(of: name), isActive: name == active,
                 hasPendingUpdate: state.pendingUpdates[name] != nil,
                 isLocated: state.isLocated(name), source: state.sourceLocation(of: name))
        }
    }

    public var rows: [Row] {
        let collection = selectedCollection
        let locked = state.isSynced(collection)
        let checks = activeChecks
        let mcps = state.store.collections[collection]?.mcps ?? [:]
        // Ordinal, which is how the popover sorts the same connectors of the same collection:
        // two surfaces over one list must agree on its order.
        return mcps.keys.sorted(by: { $0.ordinallyPrecedes($1) }).map { name in
            Row(name: name, caution: state.connectorCaution(name, in: collection), isLocked: locked,
                checked: checks.contains(name),
                target: CollectionsModel.target(of: mcps[name]?.config ?? .object([:])))
        }
    }

    /// What a row says the connector runs, never a secret: a remote connector's host; a local
    /// one's launcher and arguments, shortened. Public and pure so both platforms test the same
    /// inputs. `home` is the user's home folder, abbreviated to "~".
    ///
    /// An allowlist, not a mask: an argument is shown only when it is a URL, an explicit path or
    /// a named artefact, and every other argument — a flag, a flag's value, `KEY=value`, a header,
    /// a shell string, a bare word — is left out. A list of secret shapes to hide would leak
    /// every shape it did not foresee. The one exception is backed by a name rather than a shape:
    /// whatever follows a flag named for a secret is left out too, since a password can look
    /// like a package.
    public static func target(of config: JSONValue, home: String = NSHomeDirectory()) -> String {
        let model = FormMapper.analyze(config).model
        var (command, commandArgs) = splitCommandLine(model.command, model.args)
        // Windows' `cmd /c npx …`: what cmd runs is the launcher, and the rule applies to what
        // follows it.
        if launcherName(command).lowercased() == "cmd", let first = commandArgs.first,
           ["/c", "/k"].contains(first.lowercased()) {
            commandArgs.removeFirst()
            if !commandArgs.isEmpty {
                let inner = commandArgs.removeFirst()
                (command, commandArgs) = splitCommandLine(inner, commandArgs)
            }
        }
        let runner = launcherName(command).lowercased()
        // Read through the unwrapping and the launcher's extension, so the same bridge is a remote
        // connector however it is spelled, and on both platforms. The decoder's URL is checked
        // like any other: it vouches for a URL, not for a host free of userinfo.
        if runner == "npx",
           let remote = RemotePattern.decode(.object(["command": .string("npx"), "args": .array(commandArgs.map(JSONValue.string))])) {
            return urlOrigin(remote.url)?.host ?? remoteType
        }
        // The slot where a package runner names the server it fetches: the one place a bare
        // hyphenated word is a package rather than, as likely, a password. It can land on the
        // value of a flag named for a secret, which the first check below drops before it counts.
        let serverSlot = packageRunners.contains(runner)
            ? commandArgs.firstIndex { !startsWith($0, "-") } : nil
        let args = commandArgs.enumerated().compactMap { index, arg -> String? in
            if index > 0, isSecretNamedFlag(commandArgs[index - 1]) { return nil }
            return shown(arg, home: home, isServerSlot: index == serverSlot)
        }
        let tokens = [launcher(command)].compactMap { $0 } + args
        return tokens.joined(separator: " ")
    }

    /// Launchers whose first positional argument names the package they fetch and run.
    private static let packageRunners: Set<String> = ["npx", "uvx", "pipx", "bunx", "pnpx"]

    /// A launcher written as a whole command line — `npx -y server --token x` in one string —
    /// split into arguments the way a shell passes them: its first argument is the launcher and
    /// the rest are arguments ahead of `args`, each held to the same rule, so a flag named for a
    /// secret at its end guards `args[0]` as it would any value.
    ///
    /// A command that is a path is not tokenized, since a Windows path holds spaces. It keeps its
    /// launcher — the last component of the path, which runs to the last word holding a separator
    /// — only when the words after its first are plainly more path or plain words: none starts
    /// with `-` or `/` or holds `:` (which covers `://`), `=` or a quote. Otherwise that last
    /// component could be the tail of a packed argument, and the launcher is omitted. The packed
    /// words are never shown, but a flag named for a secret at their end, read with its quotes
    /// removed as a shell would pass it, still guards `args[0]`. A path-shaped raw secret that
    /// passes these checks (`C:\x\tool.exe abc\cd.ef`) is accepted: residual 2 in the B1 report.
    private static func splitCommandLine(_ text: String, _ args: [String]) -> (String, [String]) {
        guard isExplicitPath(text) else {
            let words = shellWords(text)
            return (words.first ?? "", Array(words.dropFirst()) + args)
        }
        let words = text.split(whereSeparator: \.isWhitespace).map(String.init)
        guard let last = words.last, words.count > 1 else { return (text, args) }
        let flag = shellWords(last).joined()
        let guarded = isSecretNamedFlag(flag) ? [flag] : []
        let isPlain = !words.dropFirst().contains {
            startsWith($0, "-") || startsWith($0, "/") || $0.contains(":") || $0.contains("=")
                || $0.contains("\"") || $0.contains("'")
        }
        let pathEnd = words.lastIndex { $0.contains("/") || $0.contains("\\") } ?? 0
        return (isPlain ? words[...pathEnd].joined(separator: " ") : "", guarded + args)
    }

    /// `text` as a shell passes it: whitespace separates arguments, a `"…"` or `'…'` run belongs
    /// to the argument it touches and loses its quotes, `\"` inside double quotes is a quote, and
    /// a backslash anywhere else is itself, as a Windows path needs. An unterminated quote runs to
    /// the end.
    private static func shellWords(_ text: String) -> [String] {
        var words: [String] = []
        var word: String.UnicodeScalarView?
        var quote: Unicode.Scalar?
        var scalars = text.unicodeScalars.makeIterator()
        var pending: Unicode.Scalar? = scalars.next()
        while let scalar = pending {
            pending = scalars.next()
            if let open = quote {
                if scalar == open {
                    quote = nil
                } else if open == "\"", scalar == "\\", pending == "\"" {
                    word?.append("\"")
                    pending = scalars.next()
                } else {
                    word?.append(scalar)
                }
            } else if scalar.properties.isWhitespace {
                if let finished = word { words.append(String(finished)) }
                word = nil
            } else if scalar == "\"" || scalar == "'" {
                quote = scalar
                if word == nil { word = String.UnicodeScalarView() }
            } else {
                if word == nil { word = String.UnicodeScalarView() }
                word?.append(scalar)
            }
        }
        if let finished = word { words.append(String(finished)) }
        return words
    }

    /// The launcher, named the way it would be typed, or nil when it could be a secret — or when
    /// it holds whitespace, a path with arguments packed into its last component.
    private static func launcher(_ command: String) -> String? {
        let name = launcherName(command)
        guard !name.isEmpty, !name.contains(where: \.isWhitespace), !name.contains("="),
              !CredentialHeuristics.looksLikeCredential(name), !looksLikeRandomToken(name) else { return nil }
        return name
    }

    /// One argument as the target column shows it, or nil when it is none of the three shapes
    /// known to be safe: a URL, as its scheme and host; an explicit path, with the home folder
    /// abbreviated; or a named artefact — a package, image, script or module. A bare word, one
    /// with no `/`, `@` or `.`, is an artefact only in the server slot.
    private static func shown(_ arg: String, home: String, isServerSlot: Bool) -> String? {
        if arg.contains("://") {
            return urlOrigin(arg).map { "\($0.scheme)://\($0.host)" }
        }
        guard !CredentialHeuristics.looksLikeCredential(arg), !looksLikeRandomToken(arg) else { return nil }
        if isExplicitPath(arg) {
            // A colon anywhere but a drive letter's is a Windows switch's value, `/p:secret`.
            let colonIsDrive = isDriveRoot(arg) && !arg.dropFirst(2).contains(":")
            guard !arg.contains("="), !arg.contains(":") || colonIsDrive else { return nil }
            let root = trimmingOneTrailingSeparator(home)
            // Case-insensitive, as both platforms' default file systems are.
            guard !root.isEmpty, let prefix = arg.range(of: root, options: [.anchored, .caseInsensitive]) else { return arg }
            let remainder = String(arg[prefix.upperBound...])
            return remainder.isEmpty || startsWith(remainder, "/") || startsWith(remainder, "\\") ? "~" + remainder : arg
        }
        guard arg.count <= 100, arg.first.map({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "@") }) == true,
              arg.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || "._@/-".contains($0)) }),
              arg.contains(where: { "/.@".contains($0) }) || (isServerSlot && arg.contains(where: { "-_".contains($0) }))
        else { return nil }
        if arg.hasPrefix("@"), let slash = arg.firstIndex(of: "/"), slash > arg.index(after: arg.startIndex) {
            let name = arg[arg.index(after: slash)...]
            if !name.isEmpty, !name.contains("/") { return "…/" + name }
        }
        return arg
    }

    /// `path` without one trailing separator of either kind.
    private static func trimmingOneTrailingSeparator(_ path: String) -> String {
        guard let last = path.unicodeScalars.last, last == "/" || last == "\\" else { return path }
        return String(String.UnicodeScalarView(path.unicodeScalars.dropLast()))
    }

    /// A random token rather than a name: with its slashes removed, at least 20 characters, with
    /// an upper-case letter, a lower-case letter and a digit, and none of `.`, `-` or `_`. An AWS
    /// secret key has this shape and a `/`, which `looksLikeCredential` refuses to consider; a
    /// real path or package name nearly always has a dot, a hyphen or no digits.
    private static func looksLikeRandomToken(_ text: String) -> Bool {
        let body = text.unicodeScalars.filter { $0 != "/" && $0 != "\\" }
        return body.count >= 20
            && body.contains { ("A"..."Z").contains($0) } && body.contains { ("a"..."z").contains($0) }
            && body.contains { ("0"..."9").contains($0) } && !body.contains { $0 == "." || $0 == "-" || $0 == "_" }
    }

    /// A flag without an attached value whose name says the next argument is a secret, whatever
    /// its dashes and case.
    private static func isSecretNamedFlag(_ arg: String) -> Bool {
        guard startsWith(arg, "-"), !arg.contains("=") else { return false }
        let name = arg.lowercased()
        return secretNames.contains { name.contains($0) }
    }

    private static let secretNames = ["token", "key", "secret", "pass", "pwd", "pw", "auth", "credential", "bearer"]

    /// Starts with `/`, `~`, `./`, `../` or a drive root (`X:\` or `X:/`). Internal rather than
    /// private because the Publish sheet offers a path row by the same rule.
    static func isExplicitPath(_ arg: String) -> Bool {
        ["/", "~", "./", "../"].contains { startsWith(arg, $0) } || isDriveRoot(arg)
    }

    /// `X:\` or `X:/`.
    private static func isDriveRoot(_ arg: String) -> Bool {
        let scalars = Array(arg.unicodeScalars.prefix(3))
        return scalars.count == 3 && (("A"..."Z").contains(scalars[0]) || ("a"..."z").contains(scalars[0]))
            && scalars[1] == ":" && (scalars[2] == "/" || scalars[2] == "\\")
    }

    /// Compared by Unicode scalar rather than by character, as the Windows mirror compares by
    /// UTF-16 unit: a combining mark after a leading `-` must not make the two disagree about
    /// whether an argument is a flag.
    private static func startsWith(_ text: String, _ prefix: String) -> Bool {
        text.unicodeScalars.starts(with: prefix.unicodeScalars)
    }

    /// The scheme and the host of the URL in `text`, the host with its port when one is written;
    /// nil when either is not plainly one. The authority runs from `://` to the first `/`, `?` or
    /// `#`, and the host is what follows its last `@`. What remains must be a bare host and
    /// optional port: a password holding an unencoded `/`, `?` or `#` ends the authority early,
    /// and whatever of it is left is refused rather than shown as the host, as is any URL with an
    /// `@` past its authority, where the boundary is in doubt. Taken from the text
    /// by hand rather than by a URL parser, because the two platforms' parsers disagree about
    /// case and default ports.
    private static func urlOrigin(_ text: String) -> (scheme: String, host: String)? {
        guard let separator = text.range(of: "://") else { return nil }
        let scheme = Array(text[..<separator.lowerBound].unicodeScalars)
        guard (2...16).contains(scheme.count), ("a"..."z").contains(scheme[0]),
              scheme.allSatisfy({ ("a"..."z").contains($0) || ("0"..."9").contains($0) || $0 == "+" })
        else { return nil }
        let rest = text[separator.upperBound...]
        let authorityEnd = rest.firstIndex(where: { "/?#".contains($0) }) ?? rest.endIndex
        // An `@` after the authority ends means the boundary cannot be trusted: `u:p/w@h` is a
        // password holding a `/`, not a host `u:p` with an `@` in its path.
        guard !rest[authorityEnd...].contains("@") else { return nil }
        let authority = rest[..<authorityEnd]
        let host = authority.lastIndex(of: "@").map { authority[authority.index(after: $0)...] } ?? authority
        let parts = host.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
        let name = parts[0].unicodeScalars
        guard !name.isEmpty,
              name.allSatisfy({ ("A"..."Z").contains($0) || ("a"..."z").contains($0) || ("0"..."9").contains($0) || $0 == "." || $0 == "-" }),
              parts.count == 1 || ((1...5).contains(parts[1].unicodeScalars.count) && parts[1].unicodeScalars.allSatisfy { ("0"..."9").contains($0) })
        else { return nil }
        return (String(text[..<separator.lowerBound]), String(host))
    }

    /// The last component of a command, splitting on both separators rather than this platform's:
    /// a collection carries connectors authored on either, and a Windows command's launcher is
    /// still worth naming on a Mac. Split by hand, because the path APIs on the two platforms
    /// disagree about which separators count. A Windows launcher's `.cmd`, `.exe` or `.bat` is
    /// dropped: `npx.cmd` is `npx`, both to name and to recognise.
    private static func launcherName(_ command: String) -> String {
        let name = command.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? command
        let isWindowsLauncher = name.count > 4 && [".cmd", ".exe", ".bat"].contains(name.suffix(4).lowercased())
        return isWindowsLauncher ? String(name.dropLast(4)) : name
    }

    public var detailLine: String {
        let collection = selectedCollection
        if state.isSynced(collection) {
            guard let source = locatedSource(of: collection) else { return CollectionsModel.unlocatedDetail }
            return CollectionsModel.syncedDetail(source, syncStatus(of: collection))
        }
        var line = CollectionsModel.localDetail(state.store.collections[collection]?.mcps.count ?? 0)
        if collection == state.activeCollection { line += CollectionsModel.activeSuffix }
        // Only this machine's binding says where the document goes, so only this machine's window
        // says it publishes. Another machine's publish record is not a fact about this one.
        if let folder = state.collectionsCache.published[collection]?.folder {
            line += " · " + CollectionsModel.publishedDetail(folder)
        }
        return line
    }

    /// The synced document as this machine can name it, or nil when it cannot: not synced, no
    /// binding, or a sidecar entry that records neither a path nor a file name. The last of those
    /// takes a hand-edited or foreign collections file — every writer here sets a file name — but
    /// the decoder accepts one, and there is nothing to refresh or to report about a document
    /// nobody can point at.
    ///
    /// Still its own rule — the detail line has a sentence of its own for an unlocated file — but
    /// the naming is `AppState.sourceLocation(of:)`'s, so the derivation lives in one place.
    private func locatedSource(of collection: String) -> String? {
        state.isLocated(collection) ? state.sourceLocation(of: collection) : nil
    }

    /// What the source is doing, in precedence order: what went wrong outranks what is waiting.
    private func syncStatus(of collection: String) -> String {
        if let failure = state.sourceErrors[collection] { return failure }
        if state.pendingUpdates[collection] != nil { return CollectionsModel.updateAvailableStatus }
        return CollectionsModel.upToDateStatus
    }

    // MARK: - Banner strip

    /// The banner above the rows, or nil. Unlike the popover's slot, which speaks for whichever
    /// collection has news, this answers only for the collection the window is showing: a strip
    /// over one collection's rows saying something about another one would be a lie. The window's
    /// button switches on this, never on AppState's, so the view depends on its model alone.
    public var banner: CollectionBanner? {
        guard let banner = state.collectionBanner,
              CollectionBannerPresentation.collection(of: banner) == selectedCollection else { return nil }
        return banner
    }

    /// The Windows mirror also carries `HasBanner`: XAML cannot bind a row's visibility to "this
    /// optional is not nil", where SwiftUI binds the optional itself.
    public var bannerText: String? { banner.map { CollectionBannerPresentation.text($0, state) } }

    public var bannerButton: String? { banner.map(CollectionBannerPresentation.button) }

    /// The strip's button. True says the news is an update, so the view has only to put the
    /// Review sheet in front of the selected collection. False says the view decides by the
    /// banner's kind: a picker for the file or folder, handed to `locateSource` or
    /// `choosePublishFolder`, or — for a publish blocked for review — the Publish sheet, since
    /// another folder is no answer to that.
    ///
    /// Deliberately not `@discardableResult`, unlike the popover's, which sets the window request
    /// on its way past: everything this one does is in the answer, so a call that drops it did
    /// nothing at all.
    public func bannerAction() -> Bool {
        guard case .updateAvailable = banner else { return false }
        return true
    }

    /// The Locate button's file, for the collection the window is showing. nil on success, else
    /// the message; also nil when the strip is not asking for a file, so a picker left open past
    /// the news it belonged to cannot point anything anywhere.
    ///
    /// This and `choosePublishFolder` return their message rather than `report` it, unlike every
    /// other verb here: they are the popover's banner verbs of the same names, and the two
    /// surfaces keep the one shape.
    public func locateSource(_ path: String) -> String? {
        guard case .locate(let collection, _) = banner else { return nil }
        return state.locateSource(for: collection, path: path)
    }

    /// The Choose Folder button's folder, for the collection the window is showing. nil as
    /// `locateSource` returns nil. Under a publish blocked for review the folder is refused inside
    /// `changePublishFolder`, which answers with the reason.
    public func choosePublishFolder(_ path: String) -> String? {
        switch banner {
        case .publishFailed(let collection, _), .publishBlocked(let collection, _):
            return state.changePublishFolder(collection, to: path)
        case .updateAvailable, .locate, nil:
            return nil
        }
    }

    // MARK: - Control state

    /// Where the selection bar's Copy to can send the ticked rows: every collection but the
    /// selected one, which is their source, in the sidebar's order, each marked whether it can
    /// take copies. A synced collection is listed but disabled: its connectors are the author's,
    /// and it has no local write path of its own to copy into.
    public var copyDestinations: [CopyDestination] {
        let collection = selectedCollection
        return state.collectionNames.filter { $0 != collection }.map { name in
            CopyDestination(name: name, isEnabled: !state.isSynced(name))
        }
    }

    public var canRemoveChecked: Bool { !state.isSynced(selectedCollection) && !checkedNames.isEmpty }

    /// Refresh reads the bound document, so it needs one this machine can name.
    public var canRefresh: Bool { locatedSource(of: selectedCollection) != nil }

    /// The last local collection stays, because only a local one takes a new connector, and the
    /// last collection of any kind stays, because the store always has an active one. A synced
    /// collection is never the last local one, so only the second rule reaches it.
    public var canDelete: Bool {
        let collection = selectedCollection
        return state.isSynced(collection)
            ? state.collectionNames.count > 1
            : state.localCollectionNames.count > 1
    }

    /// Through the rows rather than the tick set, so a tick on a connector that has since
    /// vanished from the collection is dropped instead of exported.
    public var checkedNames: [String] { rows.filter(\.checked).map(\.name) }

    // MARK: - Header pills and menu

    /// Marks an exception to the ordinary local collection: `Published` and `Subscribed` are
    /// mutually exclusive, since a subscribed collection has an author elsewhere and cannot also
    /// publish. There is no case for the default — an ordinary local collection carries no pill.
    public enum Pill: Equatable, Sendable {
        case active, published, subscribed
    }

    public static let activePill = "Active"
    public static let publishedPill = "Published"
    public static let subscribedPill = "Subscribed"

    public static func title(for pill: Pill) -> String {
        switch pill {
        case .active: return activePill
        case .published: return publishedPill
        case .subscribed: return subscribedPill
        }
    }

    /// The selected collection's pills, in the header's order: active first, then the one
    /// exception `isSynced` and `isPublished` cannot both name at once.
    public var pills: [Pill] {
        let collection = selectedCollection
        var marks: [Pill] = []
        if collection == state.activeCollection { marks.append(.active) }
        if state.isSynced(collection) { marks.append(.subscribed) }
        else if state.isPublished(collection) { marks.append(.published) }
        return marks
    }

    /// The collection's `⋯` menu (spec §4), built here so both platforms show the same list from
    /// the same flags the window's other controls already read.
    public enum MenuEntry: Hashable, Sendable {
        case makeActive, rename, duplicate, startPublishing, publishingSettings, stopPublishing,
             showPublishedFile, exportAll(enabled: Bool), makeLocalCopy, refresh, showSourceFile,
             stopSyncing, delete(enabled: Bool), separator
    }

    public static let duplicateAction = "Duplicate"
    public static let exportAllAction = "Export All"
    public static let showPublishedFileAction = "Show Published File"
    public static let showSourceFileAction = "Show Source File"

    /// The row's pencil, as its tooltip and its spoken name. It names the connector, so a screen
    /// reader moving down the list hears which one each pencil edits rather than "Edit" each time.
    public static func editLabel(for connector: String) -> String { "Edit “\(connector)”" }

    public static func title(for entry: MenuEntry) -> String {
        switch entry {
        case .makeActive: return makeActiveAction
        case .rename: return renameAction
        case .duplicate: return duplicateAction
        case .startPublishing: return publishButton
        case .publishingSettings: return publishSettingsButton
        case .stopPublishing: return stopPublishingAction
        case .showPublishedFile: return showPublishedFileAction
        case .exportAll: return exportAllAction
        case .makeLocalCopy: return makeLocalCopyButton
        case .refresh: return refreshButton
        case .showSourceFile: return showSourceFileAction
        case .stopSyncing: return stopSyncingAction
        case .delete: return deleteAction
        case .separator: return ""
        }
    }

    /// Whether an entry can be chosen. Only `exportAll` and `delete` ever arrive dimmed, and they
    /// carry the answer; every other entry the menu lists applies.
    public static func isEnabled(_ entry: MenuEntry) -> Bool {
        switch entry {
        case .exportAll(let enabled), .delete(let enabled): return enabled
        default: return true
        }
    }

    /// Lists what applies rather than dimming what does not, with one exception: `exportAll` is
    /// always present, greyed out on a synced collection, since exporting a read-only mirror is
    /// refused for a reason worth stating rather than a button worth hiding.
    public var collectionMenu: [MenuEntry] {
        let collection = selectedCollection
        let synced = state.isSynced(collection)
        var entries: [MenuEntry] = []
        if collection != state.activeCollection {
            entries.append(.makeActive)
            entries.append(.separator)
        }
        entries.append(.rename)
        entries.append(synced ? .makeLocalCopy : .duplicate)
        entries.append(.separator)
        if synced {
            if canRefresh { entries.append(.refresh) }
            if sourceFilePath != nil { entries.append(.showSourceFile) }
            entries.append(.stopSyncing)
        } else {
            let published = state.isPublished(collection)
            // Publishing Settings stays beside Stop Publishing: reopening the sheet and pressing
            // Publish again is the only way to change what is shared or to mark a path again.
            entries.append(published ? .publishingSettings : .startPublishing)
            if published { entries.append(.stopPublishing) }
            if publishedFilePath != nil { entries.append(.showPublishedFile) }
        }
        entries.append(.exportAll(enabled: !synced))
        entries.append(.separator)
        entries.append(.delete(enabled: canDelete))
        return entries
    }

    /// Duplicate: the same copy semantics as every other copy in this window — disabled, with
    /// provenance — but of the whole collection, and the selection stays where it was rather than
    /// following the new one the way `makeLocalCopy()` and `create()` do.
    @discardableResult
    public func duplicate() -> Bool {
        let collection = selectedCollection
        guard !state.isSynced(collection),
              let typed = dialogs.promptForName(title: AppState.newCollectionTitle, initial: "") else {
            lastError = nil
            return false
        }
        return report(state.makeLocalCopyOfCollection(collection, named: typed))
    }

    /// This machine's published document for the selected collection: the folder it writes to,
    /// plus the file name `publishedFileName(of:)` already derives. nil for anything not
    /// published from here, which is what the menu's `showPublishedFile` above tests for.
    public var publishedFilePath: String? {
        let collection = selectedCollection
        guard let folder = state.collectionsCache.published[collection]?.folder,
              let fileName = publishedFileName(of: collection) else { return nil }
        return URL(fileURLWithPath: folder).appendingPathComponent(fileName).path
    }

    /// A located synced collection's document — `locatedSource(of:)` again, named for the menu's
    /// `showSourceFile` and the header's own use, both of which want the same nil the detail line
    /// already turns into "file not located on this machine".
    public var sourceFilePath: String? { locatedSource(of: selectedCollection) }

    // MARK: - Sidebar and connectors header

    /// The sidebar `+`'s tooltip and accessibility label: its glyph alone does not say that
    /// what it adds is a collection.
    public static let addCollectionTooltip = "Add Collection"
    public static let importSubtitle = "Adds copies you own"
    public static let subscribeSubtitle = "Stays in sync, read-only"
    public static let connectorsHeader = "Connectors"
    public static let addConnectorTooltip = "Add Connector"
    public static let addConnectorDisabledTooltip = "Additions go in a local collection."
    /// The `⋯` button's own tooltip and accessibility label.
    public static let moreActionsLabel = "More"

    public var canAddConnector: Bool { !state.isSynced(selectedCollection) }

    public var addConnectorTooltipText: String {
        canAddConnector ? CollectionsModel.addConnectorTooltip : CollectionsModel.addConnectorDisabledTooltip
    }

    /// The `+` button on the connector list header: an Add-Remote target in the collection the
    /// window is showing.
    public func newConnectorTarget() -> EditTarget { EditTarget.newRemote(in: selectedCollection) }

    // MARK: - Rows

    /// A synced collection's rows cannot be exported, so they cannot be ticked either.
    public func setChecked(_ name: String, _ on: Bool) {
        let collection = selectedCollection
        guard !state.isSynced(collection) else { return }
        objectWillChange.send()
        if checkedCollection != collection {
            checkedNames_ = []
            checkedCollection = collection
        }
        if on { checkedNames_.insert(name) } else { checkedNames_.remove(name) }
    }

    /// The pencil: the same connector in two collections is two windows, so the target carries
    /// the collection this window is showing.
    public func editTarget(for row: String) -> EditTarget {
        let collection = selectedCollection
        let entry = state.store.collections[collection]?.mcps[row] ?? MCPEntry(config: .object([:]))
        return EditTarget.existing(name: row, entry: entry, in: collection)
    }

    /// The names the export sheet writes, in the order the rows show them. `checkedNames` today,
    /// kept as its own member so that what an export takes is decided here, in one place, rather
    /// than in each window that opens the sheet.
    public func exportIntentForChecked() -> [String] { checkedNames }

    /// Copies the ticked connectors into another local collection: they arrive disabled and record
    /// where they came from, so nothing Claude runs changes and nothing is applied. true when they
    /// landed, and the ticks go with them; false when there was nothing to copy, the destination is
    /// not one `copyDestinations` enables, or the copy failed, with the reason in `lastError` for
    /// the last.
    @discardableResult
    public func copyChecked(into collection: String, choices: [String: ImportChoice] = [:]) -> Bool {
        let names = checkedNames
        guard !names.isEmpty, copyDestinations.contains(where: { $0.name == collection && $0.isEnabled }) else {
            lastError = nil
            return false
        }
        return copy(names, into: collection, choices: choices)
    }

    /// Copy to ▸ New Collection: asks for a name, makes an empty local collection, and copies the
    /// ticked connectors into it. The window stays on the collection the rows came from and the
    /// ticks clear, exactly as a copy into an existing collection does. An empty collection rather
    /// than a copy of the active one, because the point is to start one from the ticked rows alone.
    /// true when the copies landed.
    @discardableResult
    public func copyCheckedIntoNewCollection() -> Bool {
        let names = checkedNames
        guard !names.isEmpty,
              let typed = dialogs.promptForName(title: AppState.newCollectionTitle, initial: "") else {
            lastError = nil
            return false
        }
        guard report(state.addEmptyCollection(named: typed)) else { return false }
        return copy(names, into: MasterStore.collectionName(typed), choices: [:])
    }

    /// The ticked connectors whose names `collection` already holds: the ones a copy there needs an
    /// answer for. Empty when nothing clashes, so the copy can go straight through.
    public func checkedNamesClashing(in collection: String) -> [String] {
        let held = state.store.collections[collection]?.mcps ?? [:]
        return checkedNames.filter { held[$0] != nil }.sorted(by: { $0.ordinallyPrecedes($1) })
    }

    /// Both copy verbs' shared tail: the copy itself, reported like every other verb here.
    private func copy(_ names: [String], into collection: String, choices: [String: ImportChoice]) -> Bool {
        guard report(state.makeLocalCopy(of: names, from: selectedCollection, into: collection, choices: choices)) else {
            return false
        }
        uncheck(names)
        return true
    }

    /// The ticks on `names` cleared in one go, as a copy or a removal leaves them.
    private func uncheck(_ names: [String]) {
        objectWillChange.send()
        checkedNames_.subtract(names)
    }

    /// The selection bar's Remove: asks first, names the connector when there is one and the
    /// count when there are more, and always says a copy remains in Backups.
    public func removeChecked() {
        let names = checkedNames
        guard !names.isEmpty else {
            lastError = nil
            return
        }
        guard dialogs.confirm(message: CollectionsModel.removeCheckedMessage(names),
                              informative: CollectionsModel.removeCheckedInformative,
                              primary: CollectionsModel.removeCheckedButton, destructive: true) else {
            lastError = nil
            return
        }
        state.remove(names: names, in: selectedCollection)
        // remove(names:in:) persists but does not apply, as its single-name sibling does not.
        if selectedCollection == state.activeCollection { state.applyInteractively() }
        uncheck(names)
        lastError = nil
    }

    // MARK: - Collection actions

    public func create() {
        askNameThenRetarget(title: AppState.newCollectionTitle, initial: "") { state.createCollection(named: $0) }
    }

    public func rename() {
        let collection = selectedCollection
        askNameThenRetarget(title: AppState.renameCollectionTitle, initial: collection) {
            state.renameCollection(collection, to: $0)
        }
    }

    public func delete() {
        let collection = selectedCollection
        guard dialogs.confirm(message: AppState.deleteCollectionMessage(collection), informative: nil,
                              primary: AppState.deleteButton, destructive: true) else {
            lastError = nil
            return
        }
        // The store refuses to delete the last local collection, and the last one of any kind.
        // Asked here as well as in the ⋯ menu, so a refusal cannot arrive after the publishing
        // below has already stopped. The store reports it: it refuses before it touches anything,
        // so asking it early is a no-op that still produces the right message.
        guard canDelete else { report(state.deleteCollection(named: collection)); return }
        if let fileName = publishedFileName(of: collection) {
            state.stopPublishing(collection, deleteFile: askAboutPublishedFile(fileName))
        }
        guard report(state.deleteCollection(named: collection)) else { return }
        selected = nil   // back to the active collection
    }

    /// Stop Publishing: the collection stays, and only the document in the folder is in question.
    public func stopPublishing() {
        let collection = selectedCollection
        guard state.isPublished(collection) else {
            lastError = nil
            return
        }
        // Nothing on this machine writes the document when there is no binding for it, so there
        // is no file here to offer to remove. Nor is there anything to ask while the last write
        // failed: the folder that refused it would refuse the delete too, so the question would
        // be one whose Remove cannot be honoured. The banner's own Stop Publishing says the same
        // by passing false outright.
        // Only a failed write puts the folder out of reach. A publish blocked for review never
        // touched it, so its document can still be removed and the question still stands.
        let failedWrite = state.publishError.map { $0.collection == collection && $0.kind == .writeFailed } ?? false
        let deleteFile = failedWrite
            ? false
            : publishedFileName(of: collection).map(askAboutPublishedFile) ?? false
        state.stopPublishing(collection, deleteFile: deleteFile)
        lastError = nil
    }

    /// Stop Syncing keeps every connector, every filled value and every switch, so there is
    /// nothing to warn about. It still asks, because the question is where the reassurance that
    /// nothing is lost is said; the button no longer carries it.
    public func stopSyncing() {
        let collection = selectedCollection
        guard state.isSynced(collection),
              dialogs.confirm(message: CollectionsModel.stopSyncingMessage(collection),
                              informative: CollectionsModel.stopSyncingInformative,
                              primary: CollectionsModel.stopSyncingAction, destructive: false) else {
            lastError = nil
            return
        }
        state.stopSyncing(collection)
        lastError = nil
    }

    public func refresh() {
        let collection = selectedCollection
        guard canRefresh else {
            lastError = nil
            return
        }
        state.refreshSource(for: collection)
        lastError = nil
    }

    /// The whole synced collection again as a local one the user can edit.
    public func makeLocalCopy() {
        let collection = selectedCollection
        guard state.isSynced(collection) else {
            lastError = nil
            return
        }
        askNameThenRetarget(title: AppState.newCollectionTitle, initial: collection) {
            state.makeLocalCopyOfCollection(collection, named: $0)
        }
    }

    /// Makes `name` the active collection. The active one already is: saving and applying it
    /// again would rewrite what is there for nothing, so it is left alone, and the views' double-
    /// click and Make Active need no guard of their own.
    public func switchTo(_ name: String) {
        if name != state.activeCollection { state.switchCollection(to: name) }
        lastError = nil
    }

    // MARK: - Helpers

    /// New Collection, Rename and Make Local Copy: asks for a name, hands it to `verb`, and on
    /// success moves the window to the collection the store now keeps under it, rather than
    /// dropping back to the active one. A cancelled prompt clears `lastError`, and a refusal is
    /// reported.
    private func askNameThenRetarget(title: String, initial: String, _ verb: (String) -> String?) {
        guard let typed = dialogs.promptForName(title: title, initial: initial) else {
            lastError = nil
            return
        }
        guard report(verb(typed)) else { return }
        retarget(to: MasterStore.collectionName(typed))
    }

    /// The document this machine writes for `collection`, or nil when nothing here publishes it.
    private func publishedFileName(of collection: String) -> String? {
        guard state.collectionsCache.published[collection] != nil,
              let record = state.collectionsFile.collections[collection]?.publish else { return nil }
        return CollectionDocument.fileName(slug: record.slug)
    }

    /// Default no: the view's default button is Keep, and this model only records the answer.
    private func askAboutPublishedFile(_ fileName: String) -> Bool {
        dialogs.confirm(message: CollectionsModel.deletePublishedFileQuestion(fileName), informative: nil,
                        primary: CollectionsModel.removeFileButton, cancel: CollectionsModel.keepFileButton,
                        destructive: false)
    }

    @discardableResult
    private func report(_ error: String?) -> Bool {
        lastError = error
        return error == nil
    }

    /// Stops listening to AppState. The app does not call this: the subscriptions hold `self`
    /// weakly and die with the `@StateObject`. Tests call it to prove the relays are what repaint
    /// the window.
    public func dispose() { subscriptions.removeAll() }
}
