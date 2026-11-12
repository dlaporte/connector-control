import Foundation
import Combine

/// The Restore sheet's Collections tab: the lines a collection restore's confirmation lists, one per
/// connector it changes (`CollectionRestorePlan.lines(naming:)`), which `restore_collection` lists too.
/// Each takes the name as its caller shows it.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionRestoreModel.cs
@MainActor
public final class CollectionRestoreModel: ObservableObject {
    nonisolated public static func comesBackLine(_ name: String, _ on: Bool) -> String { on ? "+ \(name) (on)" : "+ \(name) (off)" }

    nonisolated public static func changesLine(_ name: String, _ on: Bool) -> String { on ? "~ \(name) (on)" : "~ \(name) (off)" }

    /// "−" is U+2212, as `ControlMasking.removedPrefix` marks a removed line.
    nonisolated public static func goesLine(_ name: String) -> String { "− \(name)" }

    nonisolated public static func unchangedLine(_ count: Int) -> String { "= \(count) unchanged" }
}
