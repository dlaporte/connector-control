/// Mirror: windows/src/ConnectorControl.Core/FreeName.cs
/// The one "name", "name 2", "name 3", … sequence every place that has to find a name nothing
/// else answers to walks: a copy landing beside a connector, an import's Keep both, a reconcile
/// keeping a connector, and the collection a launch ingest creates.
public enum FreeName {
    /// `name`, or "name 2", "name 3", … - the first one `isTaken` turns down.
    public static func first(_ name: String, isTaken: (String) -> Bool) -> String {
        guard isTaken(name) else { return name }
        var suffix = 2
        while isTaken("\(name) \(suffix)") { suffix += 1 }
        return "\(name) \(suffix)"
    }
}
