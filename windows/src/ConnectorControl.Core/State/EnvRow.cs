namespace ConnectorControl.Core.State;

/// <summary>
/// One environment-variable row. The row object's own identity — not a stored id — is what keeps
/// it in place while the name is edited; values are masked unless <see cref="Revealed"/> (only
/// freshly added rows start revealed).
/// </summary>
public sealed class EnvRow : ObservableObject
{
    private string name;
    private string value;
    private bool revealed;
    private EditorModel? editor;

    public EnvRow(string name, string value)
    {
        this.name = name;
        this.value = value;
    }

    /// <summary>The author's published hint is keyed by the name, so it follows it.</summary>
    public string Name
    {
        get => name;
        set
        {
            if (Set(ref name, value))
            {
                Raise(nameof(PublishedHint));
            }
        }
    }

    /// <summary>Whether the value is still owed, and the hint for its marker, follow it.</summary>
    public string Value
    {
        get => value;
        set
        {
            if (Set(ref this.value, value))
            {
                Raise(nameof(Owed));
                Raise(nameof(Hint));
            }
        }
    }

    public bool Revealed { get => revealed; set => Set(ref revealed, value); }

    // MARK: the editor's rules

    // What EditorModel says about this row, as properties the row's template binds: every answer
    // is the model's, and the row only asks it. The row raises them again when its own value or
    // name moves, and the model through RaiseRules when the snapshot or the collection does. The
    // Mac's view asks the model per render instead, since its rows are structs.

    /// <summary><see cref="EditorModel.AsksFor(EnvRow)"/>: the value's box is the live placeholder one.</summary>
    public bool Asks => editor?.AsksFor(this) ?? false;

    /// <summary><see cref="EditorModel.IsOwed(EnvRow)"/>: the caution ring and the phrase under the box.</summary>
    public bool Owed => editor?.IsOwed(this) ?? false;

    /// <summary><see cref="EditorModel.PlaceholderHint(EnvRow)"/>: what the document said about finding the value.</summary>
    public string? Hint => editor?.PlaceholderHint(this);

    /// <summary><see cref="EditorModel.PublishedHint(EnvRow)"/>: what a published collection sends in the value's place.</summary>
    public string? PublishedHint => editor?.PublishedHint(this);

    /// <summary>The editor whose rules this row answers with, set as the row joins its list.</summary>
    internal void Attach(EditorModel owner)
    {
        editor = owner;
        RaiseRules();
    }

    internal void RaiseRules()
    {
        Raise(nameof(Asks));
        Raise(nameof(Owed));
        Raise(nameof(Hint));
        Raise(nameof(PublishedHint));
    }
}
