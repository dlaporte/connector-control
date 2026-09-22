import Foundation

/// How one field of a connector is named to the author.
///
/// The document names a field by its own shape — `local.args[1]`, `remote.auth.clientId` — and that
/// shape need not be the one the author edits: a connector the document calls remote opens in the
/// local form whenever its command line is not the canonical shape the remote form rebuilds. Where
/// the editor opens a connector in the local form, a field is named as that form shows it; anywhere
/// else the document's own name is given, and said to be the document's, rather than a name the
/// author will not find.
///
/// Mirror: windows/src/ConnectorControl.Core/FieldName.cs
public enum FieldName {
    public static let command = "the command"

    /// Counted from one, as the editor lists the argument rows.
    public static func argument(_ number: Int) -> String { "argument \(number)" }

    public static func envValue(_ name: String) -> String { "the value of \(name)" }

    /// A hint belongs to the Publish sheet, whichever form the editor opens.
    public static func hint(_ name: String) -> String { "the hint for \(name)" }

    public static func document(_ field: String) -> String { "the document's \(field)" }

    /// `field`, as `CollectionDocument.findings(of:)` names it, in the words of the form the editor
    /// opens `config` in. `value` is the path reported there, which finds the argument holding it
    /// when the document's field belongs to a form the editor does not show.
    public static func of(_ field: String, in config: JSONValue, holding value: String) -> String {
        if let name = between(field, "env.", ".hint") ?? between(field, "needs.", ".hint") { return hint(name) }
        // The editor's own rule for an existing connector: the remote form only for the command
        // line it can rebuild, and the local form for everything else.
        guard RemotePattern.detect(config) == nil else { return document(field) }
        if field == "local.command" { return command }
        if let index = between(field, "local.args[", "]").flatMap(Int.init) { return argument(index + 1) }
        if let name = between(field, "env.", ".value") { return envValue(name) }
        // A remote field on a connector the editor opens locally: the text sits in the command
        // line, where the author edits it. Anything else — an additional field, the platform — has
        // no row of its own, and the document's name is the honest one.
        guard field.hasPrefix("remote."), case .object(let object) = config else { return document(field) }
        var number = 0
        if case .array(let items)? = object["args"] {
            for item in items {
                guard case .string(let text) = item else { continue }
                number += 1
                if KeptValue.holds(text, value) { return argument(number) }
            }
        }
        if case .string(let text)? = object["command"], KeptValue.holds(text, value) { return command }
        return document(field)
    }

    private static func between(_ field: String, _ prefix: String, _ suffix: String) -> String? {
        guard field.hasPrefix(prefix), field.hasSuffix(suffix), field.count > prefix.count + suffix.count else { return nil }
        return String(field.dropFirst(prefix.count).dropLast(suffix.count))
    }
}
