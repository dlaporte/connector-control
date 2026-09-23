namespace ConnectorControl.Core.State;

/// <summary>A switch, the name, and an advisory caution glyph.</summary>
public sealed class ConnectorRow : ObservableObject
{
    private readonly AppState state;
    private bool enabled;
    private string? toolWarning;
    private bool isLocked;

    public ConnectorRow(AppState state, string name, bool enabled, string? toolWarning, bool isLocked = false)
    {
        this.state = state;
        Name = name;
        this.enabled = enabled;
        this.toolWarning = toolWarning;
        this.isLocked = isLocked;
    }

    public string Name { get; }

    /// <summary>
    /// The lock's tooltip, the same sentence the Collections window's rows show for the same
    /// fact, borrowed rather than written twice.
    /// </summary>
    public string LockTooltip => CollectionsModel.LockedGlyphTooltip;

    /// <summary>
    /// The caution glyph's tooltip, or null for no glyph: this connector's
    /// launcher is not where Claude Desktop looks. Advisory only — the row
    /// still toggles. <see cref="FlyoutModel"/> keeps it in step with AppState.ToolStatuses.
    /// </summary>
    public string? ToolWarning => toolWarning;

    public bool HasToolWarning => toolWarning is not null;

    /// <summary>
    /// Leads the row with a lock: this connector belongs to the author of a synced collection's
    /// document. The switch stays live; everything else is read-only.
    /// </summary>
    public bool IsLocked => isLocked;

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
    internal void Sync(bool value, string? warning, bool locked)
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
        if (isLocked != locked)
        {
            isLocked = locked;
            Raise(nameof(IsLocked));
        }
    }
}
