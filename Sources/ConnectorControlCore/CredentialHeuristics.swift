/// Warnings for the publish preview, never edits: the exporter knows env values and the remote
/// auth fields are secrets, but a token typed into an argument is indistinguishable from data.
/// These patterns catch the common shapes; a placeholder marker is by definition not a secret.
public enum CredentialHeuristics {
    static let prefixes = ["sk-", "ghp_", "xox", "Bearer "]

    public static func looksLikeCredential(_ value: String) -> Bool {
        if Placeholder.containsMarker(value) { return false }
        if prefixes.contains(where: { value.hasPrefix($0) }) { return true }
        guard value.count >= 32, !value.contains(" "), !value.contains("/") else { return false }
        let letters = value.contains { $0.isLetter }
        let digits = value.contains { $0.isNumber }
        return letters && digits
    }
}
