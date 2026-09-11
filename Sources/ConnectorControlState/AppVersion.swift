/// The Settings window's version line: the marketing version,
/// with the build number in parentheses only when it differs, or
/// "development build" when there is no bundle (bare `swift run`).
public enum AppVersion {
    public static let developmentBuild = "development build"

    public static func display(info: [String: Any]?) -> String {
        guard let short = info?["CFBundleShortVersionString"] as? String else {
            return developmentBuild
        }
        if let build = info?["CFBundleVersion"] as? String, build != short {
            return "\(short) (\(build))"
        }
        return short
    }
}
