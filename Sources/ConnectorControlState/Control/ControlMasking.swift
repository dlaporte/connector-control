import ConnectorControlCore
import Foundation

/// Mirror: windows/src/ConnectorControl.Core/State/Control/ControlMasking.cs
///
/// What a connector looks like to anyone but its owner: the command line and URL in full, with
/// every secret value replaced. Environment and header values are never shown, only whether they
/// are set, and nor is a value under a key named for a secret; an argument that reads as a
/// credential, the value after a flag named for a secret, a `NAME=value` word's value when the
/// name is a secret's, a header's value, a URL's password and query and fragment values, and a
/// URL's user name or path segment that reads as a credential, read "•••". Placeholders -
/// `${CC_NEEDS:…}` and `${COLLECTION_DIR}` - name what is missing rather than what is secret, and
/// show as written when they are the whole value. Every rule is plain string work, with no URL
/// parser, so both platforms mask the same text the same way. Structured output is masked or, for
/// the command-line tool's `show --reveal` (never Claude's), revealed; the line renderers take an
/// `Exposure`, which adds the confirmation dialog's, where an environment value shows unless its
/// variable is named for a secret and names no program to run.
/// Main-actor only: it reads `EditorModel.needsValue`, which is.
@MainActor
public enum ControlMasking {
    /// How much a line renderer shows. `masked`: what Claude and the command-line tool see.
    /// `dialog`: the confirmation dialogs, whose text never goes back to Claude - the command,
    /// arguments, URL and environment values in full, with a variable named for a secret (unless it
    /// names a program to run), header values, the OAuth client secret and every value under a key
    /// named for a secret still masked.
    /// `revealed`: everything as written, for the command-line tool's `show --reveal`.
    public enum Exposure: Sendable { case masked, dialog, revealed }

    public static let mask = "•••"
    /// A secret's state in structured output.
    public static let setState = "set"
    public static let needsValueState = "needs value"
    /// A set secret on a code line; an unset one reads `EditorModel.needsValue`.
    public static let setText = "••• (set)"
    public static let typeLabel = "type"
    public static let urlLabel = "url"
    public static let headersLabel = "headers"
    public static let oauthLabel = "oauth"
    public static let scopesLabel = "scopes"
    public static let commandLabel = "command"
    public static let argsLabel = "args"
    public static let envLabel = "env"
    public static let otherLabel = "other"
    public static let nameLabel = "name"
    public static let removedPrefix = "  − "
    public static let addedPrefix = "  + "

    public static func remoteBridgeType(_ package: String) -> String { "remote, through \(package)" }

    public static func oauthClientText(_ clientID: String, _ secret: String) -> String { "client \(clientID), secret \(secret)" }

    public static func assignment(_ name: String, _ value: String) -> String { "\(name) = \(value)" }

    /// One value on a line as `exposure` shows it: in the dialog, quoted as an argument is when it is
    /// not plain (`ControlText.word(_:)`), so a newline and the two characters \n read apart and a run
    /// of spaces cannot pass for a row of its own; elsewhere as it is.
    static func single(_ text: String, _ exposure: Exposure) -> String {
        exposure == .dialog ? ControlText.word(text) : text
    }

    // MARK: Values

    /// A value that is wholly a reference - a placeholder, `${COLLECTION_DIR}`, or a `${NAME}` /
    /// `$NAME` - takes its secret from elsewhere and holds none itself. One with a placeholder
    /// inside other text can still hold a secret beside it, so it is masked like any other.
    static func refersElsewhere(_ value: String) -> Bool {
        CredentialHeuristics.isReference(value)
    }

    /// Whether `text` reads as a credential or a random token, as written or with every `${…}`
    /// taken out: the heuristics take a placeholder for a value still to come, so
    /// `sk-live-…${CC_NEEDS:x}` would otherwise pass.
    static func looksSecret(_ text: String) -> Bool {
        [text, withoutReferences(text)].contains {
            CredentialHeuristics.looksLikeCredential($0) || CredentialHeuristics.looksLikeRandomToken($0)
        }
    }

    /// Whether a piece of a URL is a secret: it reads as one and is not wholly a reference.
    static func secretShaped(_ text: String) -> Bool {
        !refersElsewhere(text) && looksSecret(text)
    }

    /// `text` with every `${…}` taken out.
    static func withoutReferences(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        var kept: [Unicode.Scalar] = []
        var index = 0
        while index < scalars.count {
            if scalars[index] == "$", index + 1 < scalars.count, scalars[index + 1] == "{",
               let close = scalars[(index + 2)...].firstIndex(of: "}") {
                index = close + 1
            } else {
                kept.append(scalars[index])
                index += 1
            }
        }
        return string(kept[...])
    }

    /// "needs value" for an empty value or one still holding a `${CC_NEEDS:…}` marker; else "set".
    public static func secretState(_ value: String) -> String {
        value.isEmpty || Placeholder.containsMarker(value) ? needsValueState : setState
    }

    /// A secret on a code line: its value with `reveal`, else "••• (set)" or "needs your value".
    public static func secretText(_ value: String, reveal: Bool = false) -> String {
        if reveal { return value }
        return secretState(value) == setState ? setText : EditorModel.needsValue
    }

    // MARK: Cutting text

    // Platform idiom: text is cut scalar by scalar here and by UTF-16 unit on Windows, which cut
    // in the same places; a Swift `Character` would carry a combining mark after a delimiter
    // along with it and cut elsewhere.

    /// The text before and after the first `delimiter`, or nil when there is none.
    static func cut(_ text: String, at delimiter: String) -> (before: String, after: String)? {
        let scalars = Array(text.unicodeScalars)
        let mark = Array(delimiter.unicodeScalars)
        guard scalars.count >= mark.count else { return nil }
        for index in 0...(scalars.count - mark.count) where Array(scalars[index..<(index + mark.count)]) == mark {
            return (string(scalars[..<index]), string(scalars[(index + mark.count)...]))
        }
        return nil
    }

    /// The text before and after the last `delimiter`, or nil when there is none.
    static func cutLast(_ text: String, at delimiter: Unicode.Scalar) -> (before: String, after: String)? {
        let scalars = Array(text.unicodeScalars)
        guard let index = scalars.lastIndex(of: delimiter) else { return nil }
        return (string(scalars[..<index]), string(scalars[(index + 1)...]))
    }

    /// `text` in the pieces between its `delimiter`s, empty ones included.
    static func pieces(_ text: String, by delimiter: Unicode.Scalar) -> [String] {
        Array(text.unicodeScalars).split(separator: delimiter, omittingEmptySubsequences: false).map(string)
    }

    /// Whether `text` starts with "-".
    static func isFlag(_ text: String) -> Bool {
        text.unicodeScalars.first == "-"
    }

    static func string(_ scalars: ArraySlice<Unicode.Scalar>) -> String {
        var text = String.UnicodeScalarView()
        text.append(contentsOf: scalars)
        return String(text)
    }

    // MARK: Arguments and URLs

    /// `text` with a URL's password and every query and fragment value replaced by "•••", and
    /// so its user name and any path segment that reads as a credential or a random token;
    /// anything that is not `scheme://…` comes back as it is, and so does a value that is wholly
    /// a reference.
    public static func url(_ text: String, reveal: Bool = false) -> String {
        guard !reveal, let scheme = cut(text, at: "://") else { return text }
        // A user name or password may hold an unencoded `#`, `?` or `/` (`postgres://u:P#ss@db`),
        // which would end the authority below before its `@` and show the rest of it as a path,
        // a query or a fragment. Where such a user info ends cannot be told from where a path
        // holding an `@` starts, so everything up to the last `@` is masked: never more shown.
        if let user = cutLast(scheme.after, at: "@"), ["#", "?", "/"].contains(where: { cut(user.before, at: $0) != nil }) {
            return scheme.before + "://" + mask + "@" + location(user.after)
        }
        return scheme.before + "://" + location(scheme.after)
    }

    /// A URL past its `scheme://`, as `url(_:)` shows it.
    static func location(_ text: String) -> String {
        var rest = text
        var fragment: String?
        if let piece = cut(rest, at: "#") {
            rest = piece.before
            fragment = piece.after
        }
        var query: String?
        if let piece = cut(rest, at: "?") {
            rest = piece.before
            query = piece.after
        }
        var authority = rest
        var path: String?
        if let piece = cut(rest, at: "/") {
            authority = piece.before
            path = piece.after
        }
        if let piece = cutLast(authority, at: "@") {
            authority = userInfo(piece.before) + "@" + piece.after
        }
        var result = authority
        if let path {
            result += "/" + pieces(path, by: "/").map { secretShaped($0) ? mask : $0 }.joined(separator: "/")
        }
        if let query { result += "?" + pairs(query) }
        if let fragment { result += "#" + pairs(fragment) }
        return result
    }

    /// A URL's `user:password`: the password masked unless it is empty or a reference, and the
    /// user name when it reads as a credential or a random token.
    static func userInfo(_ text: String) -> String {
        guard let piece = cut(text, at: ":") else { return secretShaped(text) ? mask : text }
        let password = piece.after.isEmpty || refersElsewhere(piece.after) ? piece.after : mask
        return (secretShaped(piece.before) ? mask : piece.before) + ":" + password
    }

    /// A query's or a fragment's `name=value&…`: each value masked unless it is empty or a
    /// reference, and a piece with no "=" when it reads as a credential or a random token.
    static func pairs(_ text: String) -> String {
        pieces(text, by: "&").map { pair in
            guard let piece = cut(pair, at: "=") else { return secretShaped(pair) ? mask : pair }
            return piece.after.isEmpty || refersElsewhere(piece.after) ? pair : piece.before + "=" + mask
        }.joined(separator: "&")
    }

    /// One argument on its own: JSON through `jsonArgument(_:)`, `--header=…` through
    /// `header(_:)`, and anything else through `word(_:)`, or word by word through `shellLine(_:)`
    /// when that hides what the one word's reading hides.
    public static func argument(_ arg: String, reveal: Bool = false) -> String {
        if reveal { return arg }
        if let json = jsonArgument(arg) { return json }
        if let piece = cut(arg, at: "="), headerFlags.contains(piece.before) { return piece.before + "=" + header(piece.after) }
        let whole = word(arg)
        guard let line = shellLine(arg) else { return whole }
        // The shell line reading never shows what the one-word reading hid: it stands in when the
        // one word hid nothing, and when both hid something different, which only a URL's rules
        // do (`https://u:p@h/x --token t`), the argument is hidden whole. Anything else the one
        // word hid (`--password=correct horse`, `Bearer x`) it hid whole.
        if whole.hasSameScalars(as: arg) { return line }
        return isURL(arg) ? mask : whole
    }

    /// Whether `word(_:)` reads `arg` by the URL's rules: it holds `://` and is not a
    /// `name=value` whose name holds none.
    static func isURL(_ arg: String) -> Bool {
        guard cut(arg, at: "://") != nil else { return false }
        guard let piece = cut(arg, at: "=") else { return true }
        return cut(piece.before, at: "://") != nil
    }

    /// An argument as one word, with a shell default for a secret masked first
    /// (`defaultsMasked(_:)`): a URL through `url(_:)`, a value that is wholly a reference as
    /// written, a header through `header(_:)` (`X-Api-Key: …` after a flag of any name) when a whole
    /// word of its name is a secret's (`headerNamesASecret(_:)`) and its value is not a scope word or
    /// a path (`token:read`, Docker's `-v secrets:/run/secrets`), `name=value` with the value masked
    /// when the name is a secret's (`--api-key=…`, Docker's `-e API_KEY=…`) or a pair flag's
    /// (`--user=admin:…`) - read before a URL in the value, so the URL's rules never pass the value's
    /// start through - curl's `-uadmin:…` glued, `user:password@host` with no scheme and no
    /// whitespace as a URL's user info, and "•••" for anything that reads as a credential or a
    /// random token - a "name" that does is part of the token, so the whole word is masked.
    static func word(_ arg: String) -> String {
        let text = defaultsMasked(arg)
        if isURL(text) { return url(text) }
        if refersElsewhere(text) { return text }
        if let piece = cut(text, at: ":"), isHeaderName(piece.before), headerNamesASecret(piece.before),
           !isPlainValue(piece.after, under: piece.before) {
            return header(text)
        }
        let scalars = Array(text.unicodeScalars)
        if scalars.starts(with: "-u".unicodeScalars), let shown = gluedPair(string(scalars[2...])) { return "-u" + shown }
        if let piece = cut(text, at: "="), !looksSecret(piece.before) {
            if CredentialHeuristics.namesASecret(piece.before), !refersElsewhere(piece.after) {
                return piece.before + "=" + mask
            }
            if pairFlags.contains(piece.before), let shown = pair(piece.after) { return piece.before + "=" + shown }
            return piece.before + "=" + argument(piece.after)
        }
        if let at = cutLast(text, at: "@"), let piece = cut(at.before, at: ":"), isUserName(piece.before),
           !piece.after.isEmpty, !piece.after.unicodeScalars.contains(where: { $0.properties.isWhitespace || $0 == "/" || $0 == "\\" }),
           !at.after.unicodeScalars.contains(where: { $0.properties.isWhitespace }) {
            return userInfo(at.before) + "@" + location(at.after)
        }
        return looksSecret(text) ? mask : text
    }

    /// Whether `text` is a header's or a user's name: ASCII letters, digits, `-` and `_`, and for a
    /// user's also `.`; never empty.
    static func isHeaderName(_ text: String) -> Bool {
        !text.isEmpty && text.unicodeScalars.allSatisfy { isASCIIWordCharacter($0) || $0 == "-" }
    }

    static func isUserName(_ text: String) -> Bool {
        !text.isEmpty && text.unicodeScalars.allSatisfy { isASCIIWordCharacter($0) || $0 == "-" || $0 == "." }
    }

    /// Whether `scalar` is an ASCII letter or digit, or `_`.
    static func isASCIIWordCharacter(_ scalar: Unicode.Scalar) -> Bool {
        ("A"..."Z").contains(scalar) || ("a"..."z").contains(scalar) || ("0"..."9").contains(scalar) || scalar == "_"
    }

    /// Whether a header named `name` carries a secret: it is named for one or signs in.
    static func signsIn(_ name: String) -> Bool {
        CredentialHeuristics.namesASecret(name)
            || signInHeaders.contains(where: { CredentialHeuristics.loweredUnits(name).elementsEqual($0.utf16) })
    }

    /// The words of a header's name that name a secret or sign in.
    static let secretHeaderWords: Set<String> = [
        "token", "key", "apikey", "secret", "password", "passwd", "pass", "pwd", "pw", "auth", "authorization",
        "credential", "credentials", "bearer", "cookie", "passphrase",
    ]

    /// The words of the ASCII name `name`, lowered: split at `-`, `_`, `.`, where a lower-case letter
    /// meets an upper-case one and where a letter meets a digit (`apiKey2` is api, key, 2).
    static func nameWords(_ name: String) -> [String] {
        var words: [String] = []
        var word = String.UnicodeScalarView()
        var lowerBefore = false
        var letterBefore = false
        for scalar in name.unicodeScalars {
            if scalar == "-" || scalar == "_" || scalar == "." {
                words.append(String(word))
                word = String.UnicodeScalarView()
                lowerBefore = false
                letterBefore = false
                continue
            }
            let upper = ("A"..."Z").contains(scalar)
            if (upper && lowerBefore) || (("0"..."9").contains(scalar) && letterBefore) {
                words.append(String(word))
                word = String.UnicodeScalarView()
            }
            word.append(upper ? Unicode.Scalar(scalar.value + 32)! : scalar)
            lowerBefore = ("a"..."z").contains(scalar)
            letterBefore = upper || lowerBefore
        }
        words.append(String(word))
        return words
    }

    /// Whether a whole word of the ASCII name `name` (`nameWords(_:)`) names a secret or signs in:
    /// `X-Api-Key`, `apiKey`, `token1`, `Authorization`, `Cookie`, but not `keycloak` or `ssh-keys`.
    static func headerNamesASecret(_ name: String) -> Bool {
        nameWords(name).contains { secretHeaderWords.contains($0) }
    }

    /// Values after a `name:` that are an access scope, not a secret (`token:read`).
    static let scopeWords = ["read", "write", "admin", "readonly", "read-only", "rw", "ro", "all", "none"]

    /// The words of a name that make its value a password, whatever word it is (`password:admin`).
    static let passwordWords: Set<String> = ["password", "passwd", "passphrase", "pass", "pwd", "pw"]

    /// Whether a header-shaped word's value reads as written: a path that does not read as a secret
    /// (`secrets:/run/secrets`, but not `X-Api-Key:/Abc9…`), or a scope word under a name with no
    /// password word (`token:read`, but not `password:admin`).
    static func isPlainValue(_ value: String, under name: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespaces)
        if trimmed.unicodeScalars.first == "/" { return !looksSecret(trimmed) }
        return scopeWords.contains { CredentialHeuristics.loweredUnits(trimmed).elementsEqual($0.utf16) }
            && !nameWords(name).contains { passwordWords.contains($0) }
    }

    /// Values a single-dash option such as `-update:` or `-useLegacy:` takes, which no password is.
    static let switchWords = ["true", "false", "yes", "no", "on", "off"]

    /// curl's `-u` with its `user:password` glued on (`-uadmin:…`), as `pair(_:)` shows it; nil
    /// unless a user's name comes before the ":" - a Go-style flag such as `-url=…` or `-user=…`
    /// reads as `name=value` - and for a switch's value (`-update:true`).
    static func gluedPair(_ text: String) -> String? {
        guard let piece = cut(text, at: ":"), isUserName(piece.before),
              !switchWords.contains(where: { CredentialHeuristics.loweredUnits(piece.after).elementsEqual($0.utf16) }) else { return nil }
        return pair(text)
    }

    /// curl's flags whose value is `user:password`.
    static let pairFlags = ["-u", "--user", "--proxy-user"]

    /// A pair flag's `user:password` as a URL's user info shows it; nil for a value with no ":", a
    /// URL, which reads as one, a reference (`${CC_NEEDS:creds}`), or a user and group as Docker's
    /// `--user` takes them - a uid and a gid or a group's name (`1000:1000`, `1000:node`), or a group
    /// named as the user (`node:node`) - which read as any argument does. A uid before anything else
    /// (`123456:…`, an account number and its key) is masked.
    static func pair(_ text: String) -> String? {
        guard !refersElsewhere(text), !isURL(text), let piece = cut(text, at: ":") else { return nil }
        let digits: (String) -> Bool = { !$0.isEmpty && $0.unicodeScalars.allSatisfy { ("0"..."9").contains($0) } }
        if digits(piece.before), digits(piece.after) || isGroupName(piece.after) { return nil }
        if piece.before.hasSameScalars(as: piece.after) { return nil }
        return userInfo(text)
    }

    /// Whether `text` is shaped as a Unix group's name, `[a-z_][a-z_-]*`: no digits or capitals.
    static func isGroupName(_ text: String) -> Bool {
        guard let first = text.unicodeScalars.first, ("a"..."z").contains(first) || first == "_" else { return false }
        return text.unicodeScalars.allSatisfy { ("a"..."z").contains($0) || $0 == "_" || $0 == "-" }
    }

    /// `text` with the default of each `${NAME:-default}` or `${NAME-default}` replaced by "•••" when
    /// `NAME` is named for a secret or the default reads as one, unless the default is empty or a
    /// reference: a shell passes the default when the variable is unset, so it can be the secret.
    /// The default runs to the brace that closes its own, past any `${…}` inside it.
    static func defaultsMasked(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        var kept: [Unicode.Scalar] = []
        var index = 0
        while index < scalars.count {
            guard let (name, dash, close) = shellDefault(scalars, at: index) else {
                kept.append(scalars[index])
                index += 1
                continue
            }
            let value = string(scalars[(dash + 1)..<close])
            kept += scalars[index...dash]
            if !value.isEmpty, !refersElsewhere(value),
               CredentialHeuristics.namesASecret(string(scalars[(index + 2)..<name])) || looksSecret(value) {
                kept += mask.unicodeScalars
            } else {
                kept += scalars[(dash + 1)..<close]
            }
            kept.append("}")
            index = close + 1
        }
        return string(kept[...])
    }

    /// Where the `${NAME:-default}` or `${NAME-default}` starting at `start` ends its name, has its
    /// `-` and closes; nil where none starts there.
    static func shellDefault(_ scalars: [Unicode.Scalar], at start: Int) -> (name: Int, dash: Int, close: Int)? {
        guard scalars[start] == "$", start + 1 < scalars.count, scalars[start + 1] == "{" else { return nil }
        var name = start + 2
        while name < scalars.count, isASCIIWordCharacter(scalars[name]) { name += 1 }
        var dash = name
        if dash < scalars.count, scalars[dash] == ":" { dash += 1 }
        guard name > start + 2, dash < scalars.count, scalars[dash] == "-" else { return nil }
        var depth = 1
        for close in (dash + 1)..<scalars.count {
            if scalars[close] == "{" { depth += 1 }
            if scalars[close] == "}" { depth -= 1 }
            if depth == 0 { return (name, dash, close) }
        }
        return nil
    }

    /// An argument that is a JSON object or array - Smithery's `--config '{"apiKey":"…"}'` - with
    /// each leaf masked by its key, as a config's are, on one line; as written when nothing in it
    /// changed. JSON nested deeper than `JSONValue.parse` reads is "•••", since what it holds cannot
    /// be read, and so is text starting with `{` or `[` that is not JSON (JSON5's `{apiKey: '…'}`,
    /// broken JSON) but names a secret as a key (`keyedSecret(_:)`); other such text - an IPv6
    /// literal, a glob, a template - is nil. Nil for anything else. Before the shell line, which
    /// would split JSON at its spaces.
    static func jsonArgument(_ arg: String) -> String? {
        guard let first = arg.unicodeScalars.first(where: { !$0.properties.isWhitespace }), first == "{" || first == "[" else { return nil }
        guard JSONValue.nests(atMost: JSONValue.maxNestingDepth, arg) else { return mask }
        guard let value = try? JSONValue.parse(Data(arg.utf8)) else { return keyedSecret(arg) ? mask : nil }
        switch value {
        case .object, .array:
            let shown = compactText(leaves(value, .masked, secret: false))
            return shown.hasSameScalars(as: compactText(value)) ? arg : shown
        default:
            return nil
        }
    }

    /// Whether `text` gives a value under a key named for a secret, or under `env` or `headers`
    /// (`namesASecret(_:)`): a name of ASCII letters, digits,
    /// `_`, `-`, `$` and `.`, in quotes or not, right before a `:` or `=` (`apiKey: …`, `"token": …`,
    /// `password=…`). A `:` after a bracket or another `:` (`[::1]`, `[Net.X]::Y`) has no name.
    static func keyedSecret(_ text: String) -> Bool {
        let scalars = Array(text.unicodeScalars)
        for (index, scalar) in scalars.enumerated() where scalar == ":" || scalar == "=" {
            var end = index
            while end > 0, scalars[end - 1].properties.isWhitespace { end -= 1 }
            if end > 0, scalars[end - 1] == "\"" || scalars[end - 1] == "'" { end -= 1 }
            var start = end
            while start > 0, isASCIIWordCharacter(scalars[start - 1]) || ["-", "$", "."].contains(scalars[start - 1]) { start -= 1 }
            if start < end, namesASecret(string(scalars[start..<end])) { return true }
        }
        return false
    }

    /// An argument holding whitespace - a shell line for `sh -c` or `cmd /c`, or a whole command
    /// line written as the command - masked as `arguments(_:)` masks the words a shell would pass,
    /// so a flag named for a secret among them hides the word after it as it would in `args`. The
    /// split drops the line's quotes, so a line is rejoined, with each word quoted as the dialog
    /// quotes an argument, only when one of its words changed. A word that changed and ends in a
    /// backslash escaping the space after it (`--api-key a\ b`) runs on into the next, which is
    /// hidden too: the split reads a backslash as itself, as a Windows path needs. Nil for an
    /// argument with no whitespace, or none of whose words changed.
    static func shellLine(_ arg: String) -> String? {
        guard arg.unicodeScalars.contains(where: { $0.properties.isWhitespace }) else { return nil }
        let words = ConnectorTarget.shellWords(arg)
        var shown = arguments(words, exposure: .masked)
        for index in words.indices.dropLast() where !shown[index].hasSameScalars(as: words[index]) {
            let backslashes = words[index].unicodeScalars.reversed().prefix { $0 == "\\" }.count
            if backslashes % 2 == 1 { shown[index + 1] = mask }
        }
        return zip(words, shown).allSatisfy({ $0.hasSameScalars(as: $1) }) ? nil : ControlText.words(shown)
    }

    /// An argument list: each through `argument(_:)`, and also the value after a flag named for a
    /// secret (`--api-key x`, `--static-oauth-client-info {…}`) unless it is itself a flag, a
    /// header after `--header`, `--headers` or `-H`, and a header's value given as a word of its own
    /// (`headerValues(_:)`).
    public static func arguments(_ args: [String], reveal: Bool = false) -> [String] {
        arguments(args, exposure: reveal ? .revealed : .masked)
    }

    /// An argument list as `exposure` shows it. The dialog keeps every argument as written but a
    /// header's value and the OAuth client secret.
    static func arguments(_ args: [String], exposure: Exposure) -> [String] {
        guard exposure != .revealed else { return args }
        let values = headerValues(args)
        return args.enumerated().map { index, arg in
            if values.contains(index) { return mask }
            if index > 0 {
                let previous = args[index - 1]
                if headerFlags.contains(previous) { return header(arg) }
                if exposure == .masked, pairFlags.contains(previous), let shown = pair(arg) { return shown }
                if hidesAfter(previous, exposure), !isFlag(arg), !refersElsewhere(arg) {
                    return exposure == .dialog ? clientInfo(arg) : mask
                }
            }
            if afterScheme(args, index, exposure) { return mask }
            return exposure == .masked ? argument(arg) : dialogArgument(arg)
        }
    }

    /// Whether `args[index]` is the value after one or more scheme words that follow a flag named
    /// for a secret (`--token Bearer x`, `--auth Token Token x`, and curl's `-H'Authorization: Bearer
    /// x'` as a shell splits it, `-HAuthorization:` `Bearer` `x`): the flag hides the scheme word, so
    /// the word after it is hidden too, as `headerValues(_:)` hides a header's value past its scheme
    /// word, unless it is a flag or a reference.
    static func afterScheme(_ args: [String], _ index: Int, _ exposure: Exposure) -> Bool {
        guard exposure == .masked, index > 1, !isFlag(args[index]), !refersElsewhere(args[index]) else { return false }
        var flag = index - 1
        while flag > 0, CredentialHeuristics.isSchemeWord(args[flag]) { flag -= 1 }
        return flag < index - 1 && hidesAfter(args[flag], exposure)
    }

    /// Whether the word after `word` is a secret: after a flag `hidesNext(_:_:)` accepts, and, when
    /// masked, after a flag with `=` whose glued value leaves the secret to the next word - nothing
    /// or only a scheme word after a flag named for a secret (`--token=` `x`, `--token=Bearer` `x`),
    /// or after a header flag a header named for a secret or that signs in, with nothing or only a
    /// scheme word after its ":" (`--header=Authorization:` `Bearer` `x`, `--header=Authorization:
    /// Bearer` `x`), as `headerValues(_:)` reads a header after a bare `--header`.
    static func hidesAfter(_ word: String, _ exposure: Exposure) -> Bool {
        if hidesNext(word, exposure) { return true }
        guard exposure == .masked, let piece = cut(word, at: "="), isFlag(piece.before) else { return false }
        if hidesNext(piece.before, exposure) {
            return piece.after.isEmpty || CredentialHeuristics.isSchemeWord(piece.after)
        }
        guard headerFlags.contains(piece.before), let named = headerName(piece.after) else { return false }
        return signsIn(named.name)
    }

    /// One argument as the dialog shows it: as written, but for the value of
    /// `--static-oauth-client-info=…`, which holds the client secret, and a header's after
    /// `--header=…`.
    static func dialogArgument(_ arg: String) -> String {
        guard let piece = cut(arg, at: "=") else { return arg }
        if headerFlags.contains(piece.before) { return piece.before + "=" + header(piece.after) }
        guard hidesNext(piece.before, .dialog), !refersElsewhere(piece.after) else { return arg }
        return piece.before + "=" + clientInfo(piece.after)
    }

    /// The OAuth client info as the dialog shows it: its JSON on one line with the client secret,
    /// and any value under a key named for a secret, masked; "•••" for text that is not a JSON
    /// object, which could be the secret itself.
    static func clientInfo(_ text: String) -> String {
        guard let value = try? JSONValue.parse(Data(text.utf8)), case .object = value else { return mask }
        return compactText(leaves(value, .dialog, secret: false))
    }

    /// The flag whose value holds an OAuth client's secret.
    static let oauthClientInfoFlag = "--static-oauth-client-info"

    /// Whether the value after `flag` is a secret: after any flag named for one when masked, and
    /// only after the OAuth client info in the dialog.
    static func hidesNext(_ flag: String, _ exposure: Exposure) -> Bool {
        if exposure == .dialog { return flag == oauthClientInfoFlag }
        return isFlag(flag) && cut(flag, at: "=") == nil && CredentialHeuristics.namesASecret(flag)
    }

    /// The flags a header follows: mcp-remote's and supergateway's `--header`, mcp-proxy's
    /// `--headers`, and curl's `-H`.
    static let headerFlags = ["--header", "--headers", "-H"]

    /// Sign-in headers whose names hold no word `CredentialHeuristics.namesASecret` reads.
    static let signInHeaders = ["cookie"]

    /// The indexes of the header values among `args` given as words of their own: after a header
    /// flag, a word with no ":", or with nothing or only a scheme word after its ":", is the
    /// header's name and the next word its value (`--headers Authorization x`, `Authorization:
    /// Bearer x` as a shell splits it, `-H Authorization:Bearer x`), past a scheme word such as
    /// `Bearer`. A value is hidden when its header is named for a secret or signs in, and is not
    /// empty, a flag or a reference, as `header(_:)` shows those; another header's name and value
    /// read as any argument does, since `-H` is also a host.
    static func headerValues(_ args: [String]) -> Set<Int> {
        var values: Set<Int> = []
        for index in args.indices where headerFlags.contains(args[index]) && index + 2 < args.count {
            guard let named = headerName(args[index + 1]), signsIn(named.name) else { continue }
            var value = index + 2
            if !named.scheme, CredentialHeuristics.isSchemeWord(args[value]), value + 1 < args.count { value += 1 }
            let text = args[value]
            if !text.isEmpty, !isFlag(text), !refersElsewhere(text) { values.insert(value) }
        }
        return values
    }

    /// A header's name when `word` gives no value with it - the whole word with no ":", or what is
    /// before a ":" with nothing after it - and whether a scheme word follows its ":" instead
    /// (`Authorization:Bearer`). Nil for a word with a value.
    static func headerName(_ word: String) -> (name: String, scheme: Bool)? {
        guard let piece = cut(word, at: ":") else { return (word, false) }
        let value = piece.after.trimmingCharacters(in: .whitespaces)
        if value.isEmpty { return (piece.before, false) }
        return CredentialHeuristics.isSchemeWord(value) ? (piece.before, true) : nil
    }

    /// "Name: value" with the value masked unless it is empty or a reference.
    static func header(_ text: String) -> String {
        guard let piece = cut(text, at: ":") else { return argument(text) }
        let value = piece.after.trimmingCharacters(in: .whitespaces)
        if value.isEmpty || refersElsewhere(value) { return text }
        return piece.before + ": " + mask
    }

    // MARK: Whole configs

    /// The keys whose values are secrets by name, in lower case: every value under them is.
    static let secretKeys = ["env", "envs", "headers"]

    /// Whether `key` is one of `secretKeys`, whatever its case (`Env`, `HEADERS`).
    static func isSecretKey(_ key: String) -> Bool {
        // Platform idiom: lowered unit by unit, as CredentialHeuristics lowers, to match ToLowerInvariant.
        secretKeys.contains { CredentialHeuristics.loweredUnits(key).elementsEqual($0.utf16) }
    }

    /// Whether a key's values are secrets: `env`, `envs` and `headers` in any case wherever they
    /// sit, and any key named for a secret.
    static func namesASecret(_ key: String) -> Bool {
        isSecretKey(key) || CredentialHeuristics.namesASecret(key)
    }

    /// The config as structured output carries it, in its own shape: `env` and `headers` values
    /// become "set" or "needs value", and so does every string or number under a key named for a
    /// secret; an array is an argument list through `arguments(_:)`, and every other string goes
    /// through `argument(_:)`, a URL through `url(_:)`.
    public static func maskedConfig(_ config: JSONValue, reveal: Bool = false) -> JSONValue {
        guard !reveal else { return config }
        guard case .object(let object) = config else { return leaves(config, .masked, secret: false) }
        var out: [String: JSONValue] = [:]
        for (key, value) in object {
            if isSecretKey(key), case .object(let values) = value {
                out[key] = .object(values.mapValues { item in
                    if case .string(let text) = item { return .string(secretState(text)) }
                    return .string(setState)
                })
            } else {
                out[key] = leaves(value, .masked, secret: namesASecret(key))
            }
        }
        return .object(out)
    }

    /// `value` as `exposure` shows it: under a key named for a secret (`secret`) every string or
    /// number reads "set" or "needs value"; masked, an array is an argument list, its strings
    /// masked as `arguments(_:)` masks them and anything after a flag named for a secret too, and
    /// every other string goes through `argument(_:)`; in the dialog an array is an argument list as
    /// the dialog shows one, and the rest is as written.
    static func leaves(_ value: JSONValue, _ exposure: Exposure, secret: Bool) -> JSONValue {
        guard exposure != .revealed else { return value }
        switch value {
        case .string(let text):
            if secret { return .string(secretState(text)) }
            return exposure == .masked ? .string(argument(text)) : value
        case .int, .double:
            return secret ? .string(setState) : value
        case .array(let items):
            if secret { return .array(items.map { leaves($0, exposure, secret: true) }) }
            let shown = arguments(items.map { item -> String in
                if case .string(let text) = item { return text }
                return ""
            }, exposure: exposure)
            return .array(zip(items, shown).map { item, text in
                if case .string = item { return .string(text) }
                return leaves(item, exposure, secret: text == mask)
            })
        case .object(let object):
            var out: [String: JSONValue] = [:]
            for (key, item) in object { out[key] = leaves(item, exposure, secret: secret || namesASecret(key)) }
            return .object(out)
        case .null, .bool:
            return value
        }
    }

    // MARK: Lines

    /// One field of a config as a person reads it: its label, and each value as written (`raw`,
    /// which tells a changed secret from an unchanged one) and as shown.
    struct Field {
        let label: String
        var values: [(raw: String, shown: String)]
    }

    /// What a connector runs, one field per line, for `get_connector`'s text and the confirmation
    /// dialogs: a connector reached through mcp-remote by its URL and how it signs in; one reached
    /// by URL alone by its URL and headers; anything else by its command, arguments and environment.
    /// Every top-level key those do not show in full follows on a line of its own, so the add
    /// dialog shows everything that would run. Fields that share a label share its line.
    public static func codeLines(_ config: JSONValue, exposure: Exposure = .masked) -> [String] {
        let all = fields(config, exposure)
        var labels: [String] = []
        for field in all where !labels.contains(field.label) { labels.append(field.label) }
        return ControlText.labelled(labels.flatMap { label in
            all.filter { $0.label == label }.flatMap(\.values).enumerated().map { index, value in
                (index == 0 ? label : "", value.shown)
            }
        })
    }

    /// The fields an edit changes, for the change confirmation: each changed field's label, then
    /// "− " before each value it loses and "+ " before each it gains. A secret that changed shows
    /// as changed although both sides read "••• (set)". Every difference between the two configs
    /// shows, as every key shows on the code lines, and labels and values go through
    /// `ControlText.escaped(_:)` as the code lines' do.
    public static func changeLines(from before: JSONValue, to after: JSONValue, exposure: Exposure = .masked) -> [String] {
        let old = fields(before, exposure)
        let new = fields(after, exposure)
        var labels: [String] = []
        for field in new + old where !labels.contains(field.label) { labels.append(field.label) }
        var lines: [String] = []
        for label in labels {
            let was = old.filter { $0.label == label }.flatMap(\.values)
            let now = new.filter { $0.label == label }.flatMap(\.values)
            let lost = was.filter { item in !now.contains { same($0, item) } }
            let gained = now.filter { item in !was.contains { same($0, item) } }
            guard !lost.isEmpty || !gained.isEmpty else { continue }
            lines.append(ControlText.escaped(label))
            lines += lost.map { removedPrefix + ControlText.escaped($0.shown) }
            lines += gained.map { addedPrefix + ControlText.escaped($0.shown) }
        }
        return lines
    }

    // Platform idiom: values are compared scalar by scalar, as the Windows mirror compares them
    // ordinally; Swift's == holds canonically equivalent text equal, which would hide a change.
    static func same(_ one: (raw: String, shown: String), _ other: (raw: String, shown: String)) -> Bool {
        one.raw.hasSameScalars(as: other.raw) && one.shown.hasSameScalars(as: other.shown)
    }

    /// The fields the code lines and change lines show: the shape's own, then a field for each
    /// top-level key those do not show in full.
    static func fields(_ config: JSONValue, _ exposure: Exposure) -> [Field] {
        guard case .object(let object) = config else {
            return [Field(label: otherLabel, values: [(raw(.array([config])), valueText("", config, exposure))])]
        }
        let (fields, shown) = view(config, exposure)
        return fields + object.keys.filter { !shown.contains($0) }
            .sorted { $0.ordinallyPrecedes($1) }
            .map { key in
                // Raw with its key, so it never matches a field's raw under the same label (a
                // `url` key beside mcp-remote's URL).
                Field(label: single(key, exposure), values: [(raw(.array([.string(key), object[key] ?? .null])), valueText(key, object[key] ?? .null, exposure))])
            }
    }

    /// A top-level key's value on a line, after `leaves`: a string as itself, anything else as one
    /// line of JSON; a string under a key named for a secret as `secretText` shows it.
    static func valueText(_ key: String, _ value: JSONValue, _ exposure: Exposure) -> String {
        if case .string(let text) = value {
            if namesASecret(key) { return secretText(text, reveal: exposure == .revealed) }
            return exposure == .masked ? argument(text) : single(text, exposure)
        }
        return compactText(leaves(value, exposure, secret: namesASecret(key)))
    }

    /// A value as written, for telling one from another: every part of it, nothing shown.
    static func raw(_ value: JSONValue) -> String {
        compactText(value)
    }

    // Platform idiom: JSONEncoder with sorted keys and unescaped slashes, which the Windows mirror
    // writes byte for byte as AppleJsonFormat.EditorText without its line breaks.
    /// One line of JSON.
    static func compactText(_ value: JSONValue) -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return (try? encoder.encode(value)).map { String(decoding: $0, as: UTF8.self) } ?? ""
    }

    /// The fields of `config` as `exposure` shows them, and the top-level keys they show in full.
    static func view(_ config: JSONValue, _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        guard case .object(let object) = config else { return ([], []) }
        // Platform idiom: RemotePattern.decode on Windows also reads `cmd /c npx …`, so a Windows
        // connector shows the same lines; the Mac has no such launcher.
        if let remote = RemotePattern.decode(config) { return remoteView(object, remote, exposure) }
        if object["command"] == nil, case .string(let address)? = object["url"] { return urlView(object, address, exposure) }
        return localView(config, object, exposure)
    }

    /// A connector reached through mcp-remote: its bridge, URL and sign-in, every argument those
    /// do not show, and its environment but the variable a header reads. The sign-in shows only
    /// the flags `RemotePattern.encode` writes for it; one written any other way (spacing, a second
    /// copy, a field the decoder drops) shows among the arguments. The bridge's value as written
    /// holds the launcher, so dropping `-y` shows as a change.
    static func remoteView(_ object: [String: JSONValue], _ remote: RemoteConfig,
                           _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        let args = strings(object["args"])
        let bridge = args.firstIndex { $0.hasSameScalars(as: remote.package) } ?? 0
        let launcher = [object["command"] ?? .null] + args[...bridge].map(JSONValue.string)
        var fields = [Field(label: typeLabel, values: [(raw(.array(launcher)), remoteBridgeType(remote.package))]),
                      Field(label: urlLabel, values: [(raw(.string(remote.url)), single(url(remote.url, reveal: exposure != .masked), exposure))])]
        let reveal = exposure == .revealed
        switch remote.auth {
        case .automatic:
            break
        case .bearer(let token):
            fields.append(Field(label: headersLabel,
                                values: [(raw(.array([.string(token)])), assignment("Authorization", secretText(token, reveal: reveal)))]))
        case .header(let name, let value):
            fields.append(Field(label: headersLabel,
                                values: [(raw(.array([.string(name), .string(value)])), assignment(single(name, exposure), secretText(value, reveal: reveal)))]))
        case .oauthClient(let clientID, let clientSecret, let scopes):
            fields.append(Field(label: oauthLabel,
                                values: [(raw(.array([.string(clientID), .string(clientSecret)])),
                                          oauthClientText(single(clientID, exposure), secretText(clientSecret, reveal: reveal)))]))
            if !scopes.isEmpty { fields.append(Field(label: scopesLabel, values: [(raw(.string(scopes)), single(scopes, exposure))])) }
        }
        let signIn = signInArguments(remote.auth)
        let rest = unmodelled(Array(args.dropFirst(bridge + 2)), signIn.pairs)
        let kept = rest.compactMap { $0 }
        if !kept.isEmpty {
            fields.append(Field(label: argsLabel, values: [(raw(.array(rest.map { $0.map(JSONValue.string) ?? .null })),
                                                            ControlText.words(arguments(kept, exposure: exposure)))]))
        }
        var shown: Set<String> = ["command", "args"]
        if case .object(let env)? = object["env"], !env.isEmpty {
            let passthrough = env.filter { !signIn.variables.contains($0.key) }
            if !passthrough.isEmpty { fields.append(secretField(envLabel, passthrough, exposure, shownInDialog: true)) }
            shown.insert("env")
        }
        return (fields, shown)
    }

    /// The sign-in flags and values `RemotePattern.encode` writes for `auth`, in pairs, and the
    /// variables it sets for them.
    static func signInArguments(_ auth: RemoteAuth) -> (pairs: [[String]], variables: Set<String>) {
        // Platform idiom: encoded with the plain npx launcher, the Mac's only one; the Windows mirror
        // names it, so its pairs start where these do.
        guard case .object(let object) = RemotePattern.encode(RemoteConfig(url: "", auth: auth)) else { return ([], []) }
        let args = strings(object["args"])
        var variables: Set<String> = []
        if case .object(let env)? = object["env"] { variables = Set(env.keys) }
        // Past "-y", the bridge and the URL, encode writes nothing but sign-in pairs.
        return (stride(from: 3, to: args.count - 1, by: 2).map { [args[$0], args[$0 + 1]] }, variables)
    }

    /// The arguments after the URL with each of `signIn`'s pairs that they hold, once, replaced by
    /// nil, where the sign-in fields show it, so where a pair stands among the other arguments
    /// shows but not the order of two pairs, which mcp-remote reads by name. A pair is a flag and
    /// its value as the decoder pairs them: `--header`, `--static-oauth-client-info` or
    /// `--static-oauth-client-metadata` before another argument.
    static func unmodelled(_ args: [String], _ signIn: [[String]]) -> [String?] {
        var expected = signIn
        var kept: [String?] = []
        var index = 0
        while index < args.count {
            if signInFlags.contains(args[index]), index + 1 < args.count {
                let pair = [args[index], args[index + 1]]
                if let match = expected.firstIndex(where: { $0.count == 2 && $0[0].hasSameScalars(as: pair[0]) && $0[1].hasSameScalars(as: pair[1]) }) {
                    expected.remove(at: match)
                    kept.append(nil)
                } else {
                    kept += pair.map { $0 }
                }
                index += 2
            } else {
                kept.append(args[index])
                index += 1
            }
        }
        return kept
    }

    static let signInFlags = ["--header", oauthClientInfoFlag, "--static-oauth-client-metadata"]

    /// A connector reached by URL alone: its URL and headers.
    static func urlView(_ object: [String: JSONValue], _ address: String,
                        _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        var fields = [Field(label: typeLabel, values: [(raw(.null), ConnectorTarget.remoteType)]),
                      Field(label: urlLabel, values: [(raw(.string(address)), single(url(address, reveal: exposure != .masked), exposure))])]
        var shown: Set<String> = ["url"]
        if case .object(let headers)? = object["headers"], !headers.isEmpty {
            fields.append(secretField(headersLabel, headers, exposure))
            shown.insert("headers")
        }
        return (fields, shown)
    }

    /// Anything else: its command, arguments and environment.
    static func localView(_ config: JSONValue, _ object: [String: JSONValue],
                          _ exposure: Exposure) -> (fields: [Field], shown: Set<String>) {
        let model = FormMapper.analyze(config).model
        var fields = [Field(label: commandLabel, values: [(raw(object["command"] ?? .null),
                                                           exposure == .masked ? argument(model.command) : single(model.command, exposure))])]
        var shown: Set<String> = []
        if case .string? = object["command"] { shown.insert("command") }
        if !model.args.isEmpty {
            fields.append(Field(label: argsLabel, values: [(raw(.array(model.args.map(JSONValue.string))),
                                                            ControlText.words(arguments(model.args, exposure: exposure)))]))
        }
        if case .array(let items)? = object["args"], !items.isEmpty, items.count == model.args.count { shown.insert("args") }
        if case .object(let env)? = object["env"], !env.isEmpty {
            fields.append(secretField(envLabel, env, exposure, shownInDialog: true))
            shown.insert("env")
        }
        return (fields, shown)
    }

    /// Environment variables or headers by name, each value as `secretText` shows it; one that is
    /// not a string is set, and shows as JSON only when revealed. The dialog shows a value of
    /// `shownInDialog` values - the environment's, since a variable can change what runs
    /// (`NODE_OPTIONS`, `PATH`, `LD_PRELOAD`) - as written, a string quoted as an argument is and
    /// anything else as JSON, unless its variable is named for a secret and names no program to
    /// run; a header's stays masked.
    static func secretField(_ label: String, _ values: [String: JSONValue], _ exposure: Exposure,
                            shownInDialog: Bool = false) -> Field {
        Field(label: label, values: values.keys.sorted { $0.ordinallyPrecedes($1) }.map { name in
            let value = values[name] ?? .null
            var shown = exposure == .revealed ? compactText(value) : setText
            if case .string(let text) = value { shown = secretText(text, reveal: exposure == .revealed) }
            // A variable that names code to run shows although its name reads as a secret's
            // (`GIT_ASKPASS` holds "pass"): the person allowing must see what would run.
            if exposure == .dialog, shownInDialog,
               !CredentialHeuristics.namesASecret(name) || CredentialHeuristics.namesAProgramToRun(name) {
                shown = compactText(value)
                if case .string(let text) = value { shown = ControlText.word(text) }
            }
            return (raw(.array([.string(name), value])), assignment(single(name, exposure), shown))
        })
    }

    /// The strings of an array value; nothing else.
    static func strings(_ value: JSONValue?) -> [String] {
        guard case .array(let items)? = value else { return [] }
        return items.compactMap { item in
            if case .string(let text) = item { return text }
            return nil
        }
    }
}
