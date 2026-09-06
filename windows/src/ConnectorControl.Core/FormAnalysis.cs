namespace ConnectorControl.Core;

/// <summary><c>Lost</c> lists elements the form CANNOT represent; empty means switching JSON → Form loses nothing.</summary>
public sealed record FormAnalysis(FormModel Model, IReadOnlyList<string> Lost)
{
    public bool IsLossless => Lost.Count == 0;
}
