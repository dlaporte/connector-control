import Foundation

/// What a connector runs, as a row or an answer to Claude shows it, never a secret: the one
/// allowlist that decides how much of a connector's command line may be shown, and the shell
/// reading of a command line that the control layer's masking shares. Pure, with no window or app
/// state, so the Collections window, the control layer and the Publish sheet all read it alike.
///
/// Mirror: windows/src/ConnectorControl.Core/ConnectorTarget.cs
public enum ConnectorTarget {
    /// What a remote connector shows when its host is in doubt, or when it has no URL to read.
    public static let remoteType = "remote"

    /// What a row says the connector runs, never a secret: a remote connector's host; a local
    /// one's launcher and arguments, shortened. Public and pure so both platforms test the same
    /// inputs. `home` is the user's home folder, abbreviated to "~".
    ///
    /// An allowlist, not a mask: an argument is shown only when it is a URL, an explicit path or
    /// a named artefact, and every other argument - a flag, a flag's value, `KEY=value`, a header,
    /// a shell string, a bare word - is left out. A list of secret shapes to hide would leak
    /// every shape it did not foresee. The one exception is backed by a name rather than a shape:
    /// whatever follows a flag named for a secret is left out too, since a password can look
    /// like a package.
    public static func target(of config: JSONValue, home: String = NSHomeDirectory()) -> String {
        // A connector Claude reaches by URL has no command to read: its URL's scheme and host, as
        // a URL argument shows them, or only that it is remote when that host is in doubt.
        if case .object(let object) = config, object["command"] == nil,
           object["url"] != nil || [.string("http"), .string("sse")].contains(object["type"]) {
            guard case .string(let url)? = object["url"], let origin = urlOrigin(url) else { return remoteType }
            return "\(origin.scheme)://\(origin.host)"
        }
        let model = FormMapper.analyze(config).model
        var (command, commandArgs) = splitCommandLine(model.command, model.args)
        // Windows' `cmd /c npx …`: what cmd runs is the launcher, and the rule applies to what
        // follows it.
        if launcherName(command).lowercased() == "cmd", let first = commandArgs.first,
           ["/c", "/k"].contains(first.lowercased()) {
            commandArgs.removeFirst()
            if !commandArgs.isEmpty {
                let inner = commandArgs.removeFirst()
                (command, commandArgs) = splitCommandLine(inner, commandArgs)
            }
        }
        let runner = launcherName(command).lowercased()
        // Read through the unwrapping and the launcher's extension, so the same bridge is a remote
        // connector however it is spelled, and on both platforms. The decoder's URL is checked
        // like any other: it vouches for a URL, not for a host free of userinfo.
        if runner == "npx",
           let remote = RemotePattern.decode(.object(["command": .string("npx"), "args": .array(commandArgs.map(JSONValue.string))])) {
            return urlOrigin(remote.url)?.host ?? remoteType
        }
        // The slot where a package runner names the server it fetches: the one place a bare
        // hyphenated word is a package rather than, as likely, a password. It can land on the
        // value of a flag named for a secret, which the first check below drops before it counts.
        let serverSlot = packageRunners.contains(runner)
            ? commandArgs.firstIndex { !startsWith($0, "-") } : nil
        let args = commandArgs.enumerated().compactMap { index, arg -> String? in
            if index > 0, isSecretNamedFlag(commandArgs[index - 1]) { return nil }
            return shown(arg, home: home, isServerSlot: index == serverSlot)
        }
        let tokens = [launcher(command)].compactMap { $0 } + args
        return tokens.joined(separator: " ")
    }

    /// Launchers whose first positional argument names the package they fetch and run.
    private static let packageRunners: Set<String> = ["npx", "uvx", "pipx", "bunx", "pnpx"]

    /// A launcher written as a whole command line - `npx -y server --token x` in one string -
    /// split into arguments the way a shell passes them: its first argument is the launcher and
    /// the rest are arguments ahead of `args`, each held to the same rule, so a flag named for a
    /// secret at its end guards `args[0]` as it would any value.
    ///
    /// A command that is a path is not tokenized, since a Windows path holds spaces. It keeps its
    /// launcher - the last component of the path, which runs to the last word holding a separator -
    /// only when the words after its first are plainly more path or plain words: none starts
    /// with `-` or `/` or holds `:` (which covers `://`), `=` or a quote. Otherwise that last
    /// component could be the tail of a packed argument, and the launcher is omitted. The packed
    /// words are never shown, but a flag named for a secret at their end, read with its quotes
    /// removed as a shell would pass it, still guards `args[0]`. A path-shaped raw secret that
    /// passes these checks (`C:\x\tool.exe abc\cd.ef`) is accepted: residual 2 in the B1 report.
    private static func splitCommandLine(_ text: String, _ args: [String]) -> (String, [String]) {
        guard isExplicitPath(text) else {
            let words = shellWords(text)
            return (words.first ?? "", Array(words.dropFirst()) + args)
        }
        let words = text.split(whereSeparator: \.isWhitespace).map(String.init)
        guard let last = words.last, words.count > 1 else { return (text, args) }
        let flag = shellWords(last).joined()
        let guarded = isSecretNamedFlag(flag) ? [flag] : []
        let isPlain = !words.dropFirst().contains {
            startsWith($0, "-") || startsWith($0, "/") || $0.contains(":") || $0.contains("=")
                || $0.contains("\"") || $0.contains("'")
        }
        let pathEnd = words.lastIndex { $0.contains("/") || $0.contains("\\") } ?? 0
        return (isPlain ? words[...pathEnd].joined(separator: " ") : "", guarded + args)
    }

    /// `text` as a shell passes it, which is also how the control layer's masking reads an argument
    /// written as a whole command line: whitespace separates arguments, a `"…"` or `'…'` run belongs
    /// to the argument it touches and loses its quotes, `\"` inside double quotes is a quote, and
    /// a backslash anywhere else is itself, as a Windows path needs. An unterminated quote runs to
    /// the end.
    public static func shellWords(_ text: String) -> [String] {
        var words: [String] = []
        var word: String.UnicodeScalarView?
        var quote: Unicode.Scalar?
        var scalars = text.unicodeScalars.makeIterator()
        var pending: Unicode.Scalar? = scalars.next()
        while let scalar = pending {
            pending = scalars.next()
            if let open = quote {
                if scalar == open {
                    quote = nil
                } else if open == "\"", scalar == "\\", pending == "\"" {
                    word?.append("\"")
                    pending = scalars.next()
                } else {
                    word?.append(scalar)
                }
            } else if scalar.properties.isWhitespace {
                if let finished = word { words.append(String(finished)) }
                word = nil
            } else if scalar == "\"" || scalar == "'" {
                quote = scalar
                if word == nil { word = String.UnicodeScalarView() }
            } else {
                if word == nil { word = String.UnicodeScalarView() }
                word?.append(scalar)
            }
        }
        if let finished = word { words.append(String(finished)) }
        return words
    }

    /// The launcher, named the way it would be typed, or nil when it could be a secret - or when
    /// it holds whitespace, a path with arguments packed into its last component.
    private static func launcher(_ command: String) -> String? {
        let name = launcherName(command)
        guard !name.isEmpty, !name.contains(where: \.isWhitespace), !name.contains("="),
              !CredentialHeuristics.looksLikeCredential(name), !CredentialHeuristics.looksLikeRandomToken(name) else { return nil }
        return name
    }

    /// One argument as the target column shows it, or nil when it is none of the three shapes
    /// known to be safe: a URL, as its scheme and host; an explicit path, with the home folder
    /// abbreviated; or a named artefact - a package, image, script or module. A bare word, one
    /// with no `/`, `@` or `.`, is an artefact only in the server slot.
    private static func shown(_ arg: String, home: String, isServerSlot: Bool) -> String? {
        if arg.contains("://") {
            return urlOrigin(arg).map { "\($0.scheme)://\($0.host)" }
        }
        guard !CredentialHeuristics.looksLikeCredential(arg), !CredentialHeuristics.looksLikeRandomToken(arg) else { return nil }
        if isExplicitPath(arg) {
            // A colon anywhere but a drive letter's is a Windows switch's value, `/p:secret`.
            let colonIsDrive = isDriveRoot(arg) && !arg.dropFirst(2).contains(":")
            guard !arg.contains("="), !arg.contains(":") || colonIsDrive else { return nil }
            let root = trimmingOneTrailingSeparator(home)
            // Case-insensitive, as both platforms' default file systems are.
            guard !root.isEmpty, let prefix = arg.range(of: root, options: [.anchored, .caseInsensitive]) else { return arg }
            let remainder = String(arg[prefix.upperBound...])
            return remainder.isEmpty || startsWith(remainder, "/") || startsWith(remainder, "\\") ? "~" + remainder : arg
        }
        guard arg.count <= 100, arg.first.map({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "@") }) == true,
              arg.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || "._@/-".contains($0)) }),
              arg.contains(where: { "/.@".contains($0) }) || (isServerSlot && arg.contains(where: { "-_".contains($0) }))
        else { return nil }
        if arg.hasPrefix("@"), let slash = arg.firstIndex(of: "/"), slash > arg.index(after: arg.startIndex) {
            let name = arg[arg.index(after: slash)...]
            if !name.isEmpty, !name.contains("/") { return "…/" + name }
        }
        return arg
    }

    /// `path` without one trailing separator of either kind.
    private static func trimmingOneTrailingSeparator(_ path: String) -> String {
        guard let last = path.unicodeScalars.last, last == "/" || last == "\\" else { return path }
        return String(String.UnicodeScalarView(path.unicodeScalars.dropLast()))
    }

    /// A flag without an attached value whose name says the next argument is a secret, whatever
    /// its dashes and case.
    private static func isSecretNamedFlag(_ arg: String) -> Bool {
        guard startsWith(arg, "-"), !arg.contains("=") else { return false }
        return CredentialHeuristics.namesASecret(arg)
    }

    /// Starts with `/`, `~`, `./`, `../` or a drive root (`X:\` or `X:/`). Public rather than
    /// private because the Publish sheet offers a path row by the same rule.
    public static func isExplicitPath(_ arg: String) -> Bool {
        ["/", "~", "./", "../"].contains { startsWith(arg, $0) } || isDriveRoot(arg)
    }

    /// `X:\` or `X:/`.
    private static func isDriveRoot(_ arg: String) -> Bool {
        let scalars = Array(arg.unicodeScalars.prefix(3))
        return scalars.count == 3 && (("A"..."Z").contains(scalars[0]) || ("a"..."z").contains(scalars[0]))
            && scalars[1] == ":" && (scalars[2] == "/" || scalars[2] == "\\")
    }

    /// Compared by Unicode scalar rather than by character, as the Windows mirror compares by
    /// UTF-16 unit: a combining mark after a leading `-` must not make the two disagree about
    /// whether an argument is a flag.
    private static func startsWith(_ text: String, _ prefix: String) -> Bool {
        text.unicodeScalars.starts(with: prefix.unicodeScalars)
    }

    /// The scheme and the host of the URL in `text`, the host with its port when one is written;
    /// nil when either is not plainly one. The authority runs from `://` to the first `/`, `?` or
    /// `#`, and the host is what follows its last `@`. What remains must be a bare host and
    /// optional port: a password holding an unencoded `/`, `?` or `#` ends the authority early,
    /// and whatever of it is left is refused rather than shown as the host, as is any URL with an
    /// `@` past its authority, where the boundary is in doubt. Taken from the text
    /// by hand rather than by a URL parser, because the two platforms' parsers disagree about
    /// case and default ports.
    private static func urlOrigin(_ text: String) -> (scheme: String, host: String)? {
        guard let separator = text.range(of: "://") else { return nil }
        let scheme = Array(text[..<separator.lowerBound].unicodeScalars)
        guard (2...16).contains(scheme.count), ("a"..."z").contains(scheme[0]),
              scheme.allSatisfy({ ("a"..."z").contains($0) || ("0"..."9").contains($0) || $0 == "+" })
        else { return nil }
        let rest = text[separator.upperBound...]
        let authorityEnd = rest.firstIndex(where: { "/?#".contains($0) }) ?? rest.endIndex
        // An `@` after the authority ends means the boundary cannot be trusted: `u:p/w@h` is a
        // password holding a `/`, not a host `u:p` with an `@` in its path.
        guard !rest[authorityEnd...].contains("@") else { return nil }
        let authority = rest[..<authorityEnd]
        let host = authority.lastIndex(of: "@").map { authority[authority.index(after: $0)...] } ?? authority
        let parts = host.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
        let name = parts[0].unicodeScalars
        guard !name.isEmpty,
              name.allSatisfy({ ("A"..."Z").contains($0) || ("a"..."z").contains($0) || ("0"..."9").contains($0) || $0 == "." || $0 == "-" }),
              parts.count == 1 || ((1...5).contains(parts[1].unicodeScalars.count) && parts[1].unicodeScalars.allSatisfy { ("0"..."9").contains($0) })
        else { return nil }
        return (String(text[..<separator.lowerBound]), String(host))
    }

    /// The last component of a command, splitting on both separators rather than this platform's:
    /// a collection carries connectors authored on either, and a Windows command's launcher is
    /// still worth naming on a Mac. Split by hand, because the path APIs on the two platforms
    /// disagree about which separators count. A Windows launcher's `.cmd`, `.exe` or `.bat` is
    /// dropped: `npx.cmd` is `npx`, both to name and to recognise.
    private static func launcherName(_ command: String) -> String {
        let name = command.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? command
        let isWindowsLauncher = name.count > 4 && [".cmd", ".exe", ".bat"].contains(name.suffix(4).lowercased())
        return isWindowsLauncher ? String(name.dropLast(4)) : name
    }
}
