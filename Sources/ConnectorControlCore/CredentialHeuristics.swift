/// Warnings for the publish preview, never edits: the exporter knows env values and the remote
/// auth fields are secrets, but a token typed into an argument is indistinguishable from data.
/// These patterns catch the common shapes; a placeholder marker is by definition not a secret.
public enum CredentialHeuristics {
    static let prefixes = ["sk-", "ghp_", "xox", "Bearer "]
    private static let letterCategories: Set<Unicode.GeneralCategory> = [
        .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter,
    ]

    public static func looksLikeCredential(_ value: String) -> Bool {
        if Placeholder.containsMarker(value) { return false }
        if prefixes.contains(where: { value.hasPrefix($0) }) { return true }
        guard value.count >= 32, !value.contains(" "), !value.contains("/") else { return false }
        // By general category, as C#'s char.IsLetter and char.IsDigit read them: a letter is L*, a
        // digit is a decimal digit (Nd) — not "²", "½" or a Roman numeral, which isNumber takes.
        let letters = value.unicodeScalars.contains { letterCategories.contains($0.properties.generalCategory) }
        let digits = value.unicodeScalars.contains { $0.properties.generalCategory == .decimalNumber }
        return letters && digits
    }
}
