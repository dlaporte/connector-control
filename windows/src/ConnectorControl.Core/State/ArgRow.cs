namespace ConnectorControl.Core.State;

/// <summary>One argument text box; the row object's own identity keeps focus while the list is edited.</summary>
public sealed class ArgRow : ObservableObject
{
    private string value;

    public ArgRow(string value)
    {
        this.value = value;
    }

    public string Value { get => value; set => Set(ref this.value, value); }
}
