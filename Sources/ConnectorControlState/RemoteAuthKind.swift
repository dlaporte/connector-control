/// The four ways the Remote form can authenticate an `npx mcp-remote`
/// invocation, in picker order.
public enum RemoteAuthKind: String, CaseIterable, Sendable {
    case automatic, bearer, header, oauthClient

    public var title: String {
        switch self {
        case .automatic: return "Automatic (OAuth / none)"
        case .bearer: return "Bearer token"
        case .header: return "Custom header"
        case .oauthClient: return "OAuth client ID/secret"
        }
    }
}
