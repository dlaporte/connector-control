namespace ConnectorControl.Core;

/// <summary>One connector in the master list (Swift <c>MCPEntry</c>).</summary>
public sealed record McpEntry(bool Enabled, JsonValue Config, EditView LastEditView = EditView.Form)
{
    public McpEntry(JsonValue config) : this(true, config, EditView.Form)
    {
    }
}
