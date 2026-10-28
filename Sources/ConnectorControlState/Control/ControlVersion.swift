/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlVersion.cs
///
/// Versions as the hello carries them and the apps show them, compared by Semantic Versioning's
/// precedence. A version that cannot be read — "development build" — is newer than nothing and
/// older than nothing, so it never decides anything.
public enum ControlVersion {
    /// The SemVer at the start of `text` — "1.5.0" from the Mac's "1.5.0 (10500)", "1.5.0-preview.3"
    /// from .NET's "1.5.0-preview.3+1a2b3c4" — or nil when it starts with none.
    public static func semantic(_ text: String) -> String? {
        let scalars = Array(text.unicodeScalars)
        var end = 0
        for part in 0..<3 {
            if part > 0 {
                guard end < scalars.count, scalars[end] == "." else { return nil }
                end += 1
            }
            let start = end
            while end < scalars.count, isDigit(scalars[end]) { end += 1 }
            guard end > start else { return nil }
        }
        if end < scalars.count, scalars[end] == "-" {
            var cursor = end + 1
            while cursor < scalars.count, isDigit(scalars[cursor]) || isLetter(scalars[cursor])
                    || scalars[cursor] == "." || scalars[cursor] == "-" {
                cursor += 1
            }
            if cursor > end + 1 { end = cursor }
        }
        var version = String.UnicodeScalarView()
        version.append(contentsOf: scalars[0..<end])
        return String(version)
    }

    /// Whether `one` comes after `other`: major, minor and patch as numbers; a release after its
    /// prereleases; prerelease identifiers numbers by value, below words, words in ASCII order, and
    /// more of them after fewer.
    public static func isNewer(_ one: String, than other: String) -> Bool {
        guard let a = parts(one), let b = parts(other) else { return false }
        for (x, y) in zip(a.numbers, b.numbers) where x != y { return x > y }
        switch (a.prerelease.isEmpty, b.prerelease.isEmpty) {
        case (true, true): return false
        case (true, false): return true
        case (false, true): return false
        case (false, false):
            for (x, y) in zip(a.prerelease, b.prerelease) {
                let order = compare(x, y)
                if order != 0 { return order > 0 }
            }
            return a.prerelease.count > b.prerelease.count
        }
    }

    static func parts(_ text: String) -> (numbers: [UInt64], prerelease: [String])? {
        guard let version = semantic(text) else { return nil }
        let pieces = version.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        let numbers = pieces[0].split(separator: ".").compactMap { UInt64($0) }
        guard numbers.count == 3 else { return nil }
        let prerelease = pieces.count > 1
            ? pieces[1].split(separator: ".", omittingEmptySubsequences: false).map(String.init) : []
        return (numbers, prerelease)
    }

    /// One prerelease identifier against another: below zero, zero or above zero. An identifier is a
    /// number only when it is all ASCII digits — "-0" is a word on both platforms — and numbers
    /// compare by value without being parsed, so none is too long to compare.
    static func compare(_ one: String, _ other: String) -> Int {
        switch (isNumber(one), isNumber(other)) {
        case (true, true):
            let a = withoutLeadingZeros(one), b = withoutLeadingZeros(other)
            if a.count != b.count { return a.count < b.count ? -1 : 1 }
            return a == b ? 0 : (a < b ? -1 : 1)
        case (true, false): return -1
        case (false, true): return 1
        case (false, false): return one == other ? 0 : (one < other ? -1 : 1)
        }
    }

    static func isNumber(_ text: String) -> Bool { !text.isEmpty && text.unicodeScalars.allSatisfy(isDigit) }

    /// Digits without their leading zeros: "" for zero, as the Windows mirror's TrimStart('0') leaves it.
    static func withoutLeadingZeros(_ digits: String) -> Substring { digits.drop { $0 == "0" } }

    static func isDigit(_ scalar: Unicode.Scalar) -> Bool { scalar.value >= 0x30 && scalar.value <= 0x39 }

    static func isLetter(_ scalar: Unicode.Scalar) -> Bool {
        (scalar.value >= 0x41 && scalar.value <= 0x5A) || (scalar.value >= 0x61 && scalar.value <= 0x7A)
    }
}
