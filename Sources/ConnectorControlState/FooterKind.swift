/// Catalog §2.5: the popover footer shows at most one button; a failed apply takes precedence.
public enum FooterKind: Equatable, Sendable {
    case hidden
    case retryApply
    case restartRequired
}
