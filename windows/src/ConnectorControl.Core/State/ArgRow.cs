namespace ConnectorControl.Core.State;

/// <summary>One argument text box; the row object's own identity keeps focus while the list is edited.</summary>
public sealed class ArgRow : ObservableObject
{
    private string value;
    private EditorModel? editor;

    public ArgRow(string value)
    {
        this.value = value;
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

    // MARK: the editor's rules

    // What EditorModel says about this argument, as properties the row's template binds, for the
    // reason EnvRow gives. The Mac's model takes an argument's index, which is what its view has,
    // and resolves it through the row's id; here a row asks by itself.

    /// <summary><see cref="EditorModel.AsksFor(ArgRow)"/>: the box is the live placeholder one.</summary>
    public bool Asks => editor?.AsksFor(this) ?? false;

    /// <summary><see cref="EditorModel.IsLive"/>: whether the box takes typing in this form.</summary>
    public bool Live => editor?.IsLive(this) ?? true;

    /// <summary><see cref="EditorModel.IsOwed(ArgRow)"/>: the caution ring and the phrase under the box.</summary>
    public bool Owed => editor?.IsOwed(this) ?? false;

    /// <summary><see cref="EditorModel.PlaceholderHint(ArgRow)"/>: what the document said about finding the path.</summary>
    public string? Hint => editor?.PlaceholderHint(this);

    /// <summary><see cref="EditorModel.PublishedHint(ArgRow)"/>: what a published collection sends in its place.</summary>
    public string? PublishedHint => editor?.PublishedHint(this);

    /// <summary><see cref="EditorModel.ArgumentNumber"/> for where the row sits now: what it shows beside its box.</summary>
    public string Number => editor?.Args.IndexOf(this) is { } index and >= 0 ? EditorModel.ArgumentNumber(index) : string.Empty;

    /// <summary><see cref="EditorModel.ArgumentLabel"/> for where the row sits now: its box's spoken name.</summary>
    public string Label => editor?.Args.IndexOf(this) is { } index and >= 0 ? EditorModel.ArgumentLabel(index) : string.Empty;

    /// <summary>The editor whose rules this row answers with, set as the row joins its list.</summary>
    internal void Attach(EditorModel owner)
    {
        editor = owner;
        RaiseRules();
    }

    /// <summary>A row joined, left or moved, so every row's position may have changed.</summary>
    internal void RaisePosition()
    {
        Raise(nameof(Number));
        Raise(nameof(Label));
    }

    internal void RaiseRules()
    {
        Raise(nameof(Asks));
        Raise(nameof(Live));
        Raise(nameof(Owed));
        Raise(nameof(Hint));
        Raise(nameof(PublishedHint));
    }
}
