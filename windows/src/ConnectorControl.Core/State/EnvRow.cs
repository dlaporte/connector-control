namespace ConnectorControl.Core.State;

/// <summary>
/// One environment-variable row (catalog §3.6). Rows carry a stable identity while the name is
/// edited; values are masked unless <see cref="Revealed"/> (only freshly added rows start revealed).
/// </summary>
public sealed class EnvRow : ObservableObject
{
    private string name;
    private string value;
    private bool revealed;

    public EnvRow(string name, string value)
    {
        this.name = name;
        this.value = value;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get => name; set => Set(ref name, value); }

    public string Value { get => value; set => Set(ref this.value, value); }

    public bool Revealed { get => revealed; set => Set(ref revealed, value); }
}
