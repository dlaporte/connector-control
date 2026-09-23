import Combine
import Foundation
import ConnectorControlCore

/// The copy clash sheet: opened only when `CollectionsModel.checkedNamesClashing(in:)` says the
/// ticked connectors are not all clean, to ask what to do about the ones the destination already
/// holds. Not `ImportModel`, which is bound to a document path — the copy already has its own
/// engine, `CollectionsModel.copyChecked(into:choices:)`; this model only builds the rows and
/// hands its answers back to it.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CopyModel.cs
@MainActor
public final class CopyModel: ObservableObject {
    /// One ticked connector against the destination. A clash needs a choice; a clean arrival
    /// needs only its badge, which is why a clashing row carries none — its picker says enough.
    public struct Row: Identifiable, Equatable, Sendable {
        public let id: String
        public let name: String
        /// True when the destination already holds this name.
        public let clashes: Bool
        /// `.keepBoth` by default, clash or not: `AppState.makeLocalCopy`'s own default for a
        /// collision nothing is said about, so silence here lands the copy beside the original
        /// rather than losing it.
        public var choice: ImportChoice
        /// What a clean row says about itself; empty for a clashing one, whose picker is the row's
        /// answer instead of a badge.
        public let badge: String

        public init(name: String, clashes: Bool, choice: ImportChoice, badge: String) {
            self.id = name
            self.name = name
            self.clashes = clashes
            self.choice = choice
            self.badge = badge
        }
    }

    public static func title(_ destination: String) -> String { "Copy to \u{201C}\(destination)\u{201D}" }
    public static let copyButton = "Copy"

    public let destination: String
    @Published public var rows: [Row]

    private let collections: CollectionsModel

    public init(collections: CollectionsModel, destination: String) {
        self.collections = collections
        self.destination = destination
        let clashing = Set(collections.checkedNamesClashing(in: destination))
        // The ticks' own display order, not a re-sort: the sheet asks about the same rows the
        // window just showed, in the order the user saw them.
        rows = collections.checkedNames.map { name in
            let clashes = clashing.contains(name)
            return Row(name: name, clashes: clashes, choice: .keepBoth,
                       badge: clashes ? "" : ImportModel.newBadge)
        }
    }

    /// Hands every clashing row's choice to the copy engine and leaves a clean row to it: nothing
    /// is in its way, so `makeLocalCopy` needs nothing said about it. true when the copies landed.
    @discardableResult
    public func perform() -> Bool {
        var choices: [String: ImportChoice] = [:]
        for row in rows where row.clashes {
            choices[row.name] = row.choice
        }
        return collections.copyChecked(into: destination, choices: choices)
    }
}
