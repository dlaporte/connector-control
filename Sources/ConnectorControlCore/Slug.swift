/// File names for collection documents: lowercase ASCII letters and digits, every other run
/// collapsed to one hyphen, so a Mac, a PC and a case-sensitive git server agree on the name.
public enum Slug {
    public static func make(_ name: String) -> String {
        var out = ""
        var pendingHyphen = false
        for scalar in name.lowercased().unicodeScalars {
            let v = scalar.value
            let isAlnum = (v >= 0x61 && v <= 0x7A) || (v >= 0x30 && v <= 0x39)
            if isAlnum {
                if pendingHyphen, !out.isEmpty { out.append("-") }
                pendingHyphen = false
                out.unicodeScalars.append(scalar)
            } else {
                pendingHyphen = true
            }
        }
        return out.isEmpty ? "collection" : out
    }
}
