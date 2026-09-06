namespace ConnectorControl.Core.State;

/// <summary>Catalog §2.5: the popover footer shows at most one button; a failed apply takes precedence.</summary>
public enum FooterKind
{
    None,
    RetryApply,
    RestartRequired,
}
