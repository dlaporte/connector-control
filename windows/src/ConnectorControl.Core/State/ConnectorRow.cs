namespace ConnectorControl.Core.State;

/// <summary>A switch, the name, an advisory caution glyph, and a pencil button.</summary>
public sealed class ConnectorRow : ObservableObject
{
    private readonly AppState state;
    private bool enabled;
    private string? toolWarning;

    public ConnectorRow(AppState state, string name, bool enabled, string? toolWarning)
    {
        this.state = state;
        Name = name;
        this.enabled = enabled;
        this.toolWarning = toolWarning;
    }

    public string Name { get; }

    public string EditTooltip => $"Edit “{Name}”";

    /// <summary>
    /// The caution glyph's tooltip, or null for no glyph: this connector's
    /// launcher is not where Claude Desktop looks. Advisory only — the row
    /// still toggles. <see cref="FlyoutModel"/> keeps it in step with AppState.ToolStatuses.
    /// </summary>
    public string? ToolWarning => toolWarning;

    public bool HasToolWarning => toolWarning is not null;

    /// <summary>The switch: setting it persists and applies immediately.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }
            enabled = value;
            Raise();
            state.SetEnabled(Name, value);
        }
    }

    /// <summary>Refresh from the store and the tool cache without calling back into AppState.</summary>
    internal void Sync(bool value, string? warning)
    {
        if (enabled != value)
        {
            enabled = value;
            Raise(nameof(Enabled));
        }
        if (toolWarning != warning)
        {
            toolWarning = warning;
            Raise(nameof(ToolWarning));
            Raise(nameof(HasToolWarning));
        }
    }
}
