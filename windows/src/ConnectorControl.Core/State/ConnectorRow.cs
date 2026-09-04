namespace ConnectorControl.Core.State;

/// <summary>Catalog §2.4 MCPRow: a switch, the name, and a pencil button.</summary>
public sealed class ConnectorRow : ObservableObject
{
    private readonly AppState state;
    private bool enabled;

    public ConnectorRow(AppState state, string name, bool enabled)
    {
        this.state = state;
        Name = name;
        this.enabled = enabled;
    }

    public string Name { get; }

    public string EditTooltip => $"Edit “{Name}”";

    /// <summary>The switch: setting it persists and applies immediately (catalog §2.4).</summary>
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

    /// <summary>Refresh from the store without calling back into AppState.</summary>
    internal void Sync(bool value)
    {
        if (enabled != value)
        {
            enabled = value;
            Raise(nameof(Enabled));
        }
    }
}
