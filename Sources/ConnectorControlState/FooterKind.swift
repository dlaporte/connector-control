/// Catalog §2.5: the popover footer shows at most one button; a failed apply takes precedence.
public enum FooterKind: Equatable, Sendable {
    case none
    case retryApply
    case restartRequired
}
