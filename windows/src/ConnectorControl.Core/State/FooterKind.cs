namespace ConnectorControl.Core.State;

/// <summary>The popover footer shows at most one button; a failed apply takes precedence.</summary>
public enum FooterKind
{
    Hidden,
    RetryApply,
    RestartRequired,
}
