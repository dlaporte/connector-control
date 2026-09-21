import Combine
import Foundation
import ConnectorControlCore

/// The Review & Apply sheet: what one synced collection's source would change, connector by
/// connector, and the one button that lets it land. The sheet shows the JSON both sides of each
/// change, because every entry here is a command Claude will run.
///
/// Mirror: windows/src/ConnectorControl.Core/State/ReviewModel.cs
@MainActor
public final class ReviewModel: ObservableObject {
    public static let applyButton = "Apply"
    public static let addedLabel = "Added"
    public static let removedLabel = "Removed"
    public static let changedLabel = "Changed"

    public static func title(_ collection: String) -> String { "Update to \(collection)" }

    public enum Kind: Equatable, Sendable { case added, removed, changed }

    /// One connector's before and after, as the editor would show them. A missing side is the
    /// side that does not exist: nothing before an addition, nothing after a removal.
    public struct Row: Identifiable, Equatable {
        public let id: String
        public let name: String
        public let kind: Kind
        public let before: String?
        public let after: String?

        public init(name: String, kind: Kind, before: String?, after: String?) {
            self.id = name
            self.name = name
            self.kind = kind
            self.before = before
            self.after = after
        }
    }

    public let collection: String
    @Published public private(set) var rows: [Row] = []

    private let state: AppState

    public init(state: AppState, collection: String) {
        self.state = state
        self.collection = collection
        rebuild()
    }

    public var title: String { ReviewModel.title(collection) }

    /// The same sentence the banner and the notification carry, so the sheet the user just
    /// opened says what the thing they clicked said.
    public var summary: String { state.pendingUpdates[collection]?.summary() ?? "" }

    /// True when the update landed. A sheet whose update has already been applied elsewhere
    /// reports success too: there is nothing left to do and nothing went wrong.
    public func apply() -> Bool {
        let error = state.applyPendingUpdate(for: collection)
        rebuild()
        return error == nil
    }

    /// Added first, then removed, then changed, each alphabetical — the order the summary
    /// sentence reads in, so the list under it is in the same order.
    private func rebuild() {
        guard let diff = state.pendingUpdates[collection] else {
            rows = []
            return
        }
        let rendered = state.pendingDocument(for: collection)?.connectors ?? [:]
        let current = state.store.collections[collection]?.mcps ?? [:]
        func text(_ config: JSONValue?) -> String? { config?.editorText() }
        rows = diff.added.map { Row(name: $0, kind: .added, before: nil, after: text(rendered[$0]?.config)) }
            + diff.removed.map { Row(name: $0, kind: .removed, before: text(current[$0]?.config), after: nil) }
            + diff.changed.map { Row(name: $0, kind: .changed, before: text(current[$0]?.config), after: text(rendered[$0]?.config)) }
    }
}
