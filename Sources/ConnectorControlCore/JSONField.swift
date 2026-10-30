/// Mirror: windows/src/ConnectorControl.Core/JsonField.cs
/// The field checks a decoder of our own files runs: one copy of the logic and the wording, shared
/// by `CollectionDocument`, `CollectionsFile` and `CollectionsLocalCache`. Every failure names the
/// key it read, through `malformed`, which makes the reading file's own error.
enum JSONField {
    static func requiredString(_ value: JSONValue?, _ what: String, _ malformed: (String) -> Error) throws -> String {
        guard let value else { throw malformed("\(what) is missing") }
        guard case .string(let s) = value else { throw malformed("\(what) is not a string") }
        return s
    }

    static func optionalString(_ value: JSONValue?, _ what: String, _ malformed: (String) -> Error) throws -> String? {
        guard let value, value != .null else { return nil }
        guard case .string(let s) = value else { throw malformed("\(what) is not a string") }
        return s
    }

    static func objectValue(_ value: JSONValue?, _ what: String,
                            _ malformed: (String) -> Error) throws -> [String: JSONValue] {
        guard let value else { return [:] }
        guard case .object(let object) = value else { throw malformed("\(what) is not a JSON object") }
        return object
    }
}
