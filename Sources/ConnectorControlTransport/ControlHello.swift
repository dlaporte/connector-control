import Foundation
import ConnectorControlCore

/// Mirror: windows/src/ConnectorControl.Core/Transport/ControlHello.cs
///
/// The first line on every connection to the app - who is calling and which version it is,
/// `{"client":"cli"|"relay","version":"1.5.0"}`. The command-line tool is trusted as the user and
/// the relay Claude runs is gated, so only a well-formed command-line hello - an object whose
/// "client" is exactly "cli" and whose "version" is text - makes a connection the command line;
/// anything else is the relay. Other keys are ignored, so a later tool can say more without being
/// gated for it.
public struct ControlHello: Equatable, Sendable {
    public static let cliClient = "cli"
    public static let relayClient = "relay"
    /// What a connection that gave no usable hello is taken for: the relay, of no known version.
    public static let unknown = ControlHello(isCLI: false, version: nil)

    public let isCLI: Bool
    /// The version the hello gave, or nil when it gave none.
    public let version: String?

    public init(isCLI: Bool, version: String?) {
        self.isCLI = isCLI
        self.version = version
    }

    /// The hello `client` sends, without its newline.
    public static func line(client: String, version: String) -> String {
        JSONValue.object(["client": .string(client), "version": .string(version)]).compactLine()
    }

    /// The hello a connection's first line gives. It fails closed: anything not read as a
    /// well-formed hello is `unknown`, the relay. The bytes are read as strictly as a message's, so
    /// one that is not UTF-8 is never decoded lossily into a hello.
    public static func parse(_ bytes: Data) -> ControlHello {
        guard bytes.count <= McpSession.maxMessageBytes, let text = McpSession.strictUTF8(bytes),
              McpSession.nests(atMost: McpSession.maxNestingDepth, text),
              case .object(let fields)? = try? JSONValue.parse(Data(text.utf8)),
              case .string(let client)? = fields["client"],
              case .string(let version)? = fields["version"], !version.isEmpty else { return .unknown }
        return ControlHello(isCLI: client == cliClient, version: version)
    }
}
