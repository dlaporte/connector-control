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

    /// Whether a name says what it names is a secret, whatever its case: a flag's (`--api-key`), a
    /// header's (`Authorization`, `X-Api-Key`) or a URL parameter's (`access_token`). The
    /// Collections window's target column leaves out whatever follows a flag named so, and the
    /// publish preview flags a header or a URL parameter named so.
    public static func namesASecret(_ name: String) -> Bool {
        let lowered = name.lowercased()
        return secretNames.contains { lowered.contains($0) }
    }

    private static let secretNames = ["token", "key", "secret", "pass", "pwd", "pw", "auth", "credential", "bearer"]

    /// Whether `value` takes its secret from somewhere else rather than holding it: one or more
    /// `${NAME}` references — an environment variable, a `${CC_NEEDS:NAME}` placeholder — with, beside
    /// them, at most one word of letters, an authentication scheme such as `Bearer`. Walked one
    /// Unicode scalar at a time, as the Windows mirror walks UTF-16 units.
    public static func isReference(_ value: String) -> Bool {
        let scalars = Array(value.unicodeScalars)
        var rest: [Unicode.Scalar] = []
        var references = 0
        var index = 0
        while index < scalars.count {
            if scalars[index] == "$", index + 1 < scalars.count, scalars[index + 1] == "{",
               let close = scalars[(index + 2)...].firstIndex(of: "}"), close > index + 2,
               scalars[(index + 2)..<close].allSatisfy({ isASCIILetterOrDigit($0) || $0 == "_" || $0 == ":" }) {
                references += 1
                index = close + 1
            } else {
                rest.append(scalars[index])
                index += 1
            }
        }
        let words = rest.split(separator: " ")
        return references > 0 && words.count <= 1 && words.allSatisfy { $0.allSatisfy { ("A"..."Z").contains($0) || ("a"..."z").contains($0) } }
    }

    private static func isASCIILetterOrDigit(_ scalar: Unicode.Scalar) -> Bool {
        ("A"..."Z").contains(scalar) || ("a"..."z").contains(scalar) || ("0"..."9").contains(scalar)
    }

    /// A random token rather than a name: with its slashes removed, at least 20 characters, with
    /// an upper-case letter, a lower-case letter and a digit, and none of `.`, `-` or `_`. An AWS
    /// secret key has this shape and a `/`, which `looksLikeCredential` refuses to consider; a
    /// real path or package name nearly always has a dot, a hyphen or no digits. The Collections
    /// window's target column leaves such a word out, and the publish preview flags one in a URL.
    public static func looksLikeRandomToken(_ text: String) -> Bool {
        let body = text.unicodeScalars.filter { $0 != "/" && $0 != "\\" }
        return body.count >= 20
            && body.contains { ("A"..."Z").contains($0) } && body.contains { ("a"..."z").contains($0) }
            && body.contains { ("0"..."9").contains($0) } && !body.contains { $0 == "." || $0 == "-" || $0 == "_" }
    }
}
