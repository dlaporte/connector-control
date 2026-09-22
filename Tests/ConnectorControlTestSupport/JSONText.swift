import Foundation

/// Whether the JSON text in `data` holds `text` anywhere, as written or as a JSON string spells
/// it. The writers on both platforms escape "/" as "\/" and "\" as "\\", so a plain search for a
/// path in a written document never finds one, and an absence check built on it can never fail.
/// Every "the document does not carry this" assertion goes through here.
///
/// Mirror: windows/tests/ConnectorControl.Core.Tests/TestSupport/JsonText.cs
public func jsonText(_ data: Data, contains text: String) -> Bool {
    let raw = String(decoding: data, as: UTF8.self)
    let escaped = text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    return [text, text.replacingOccurrences(of: "/", with: "\\/"),
            escaped, escaped.replacingOccurrences(of: "/", with: "\\/")].contains { raw.contains($0) }
}

/// `jsonText(_:contains:)` for JSON already in hand as a string, such as a preview.
public func jsonText(_ json: String, contains text: String) -> Bool {
    jsonText(Data(json.utf8), contains: text)
}

/// `jsonText(_:contains:)` for the document written at `url`.
public func jsonFile(_ url: URL, contains text: String) throws -> Bool {
    jsonText(try Data(contentsOf: url), contains: text)
}
