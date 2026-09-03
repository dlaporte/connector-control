namespace ConnectorControl.Core;

/// <summary>Swift <c>ClaudeConfigError.malformed(detail)</c>.</summary>
public sealed class ClaudeConfigException : Exception
{
    public string Detail { get; }

    public ClaudeConfigException(string detail) : base(detail)
    {
        Detail = detail;
    }
}
