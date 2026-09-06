namespace ConnectorControl.Core;

/// <summary>What the Local form edits (Swift <c>FormModel</c>).</summary>
public sealed class FormModel : IEquatable<FormModel>
{
    public string Command { get; }
    public IReadOnlyList<string> Args { get; }
    public IReadOnlyDictionary<string, string> Env { get; }
    /// <summary>Keys the form has no widget for — preserved verbatim, shown read-only.</summary>
    public IReadOnlyDictionary<string, JsonValue> Additional { get; }

    public FormModel(
        string command = "",
        IEnumerable<string>? args = null,
        IEnumerable<KeyValuePair<string, string>>? env = null,
        IEnumerable<KeyValuePair<string, JsonValue>>? additional = null)
    {
        Command = command;
        Args = args?.ToList() ?? [];
        Env = env is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(env, StringComparer.Ordinal);
        Additional = additional is null ? new Dictionary<string, JsonValue>(StringComparer.Ordinal) : new Dictionary<string, JsonValue>(additional, StringComparer.Ordinal);
    }

    public bool Equals(FormModel? other) =>
        other is not null
        && string.Equals(Command, other.Command, StringComparison.Ordinal)
        && Args.SequenceEqual(other.Args, StringComparer.Ordinal)
        && DictionaryEquality.Equal(Env, other.Env)
        && DictionaryEquality.Equal(Additional, other.Additional);

    public override bool Equals(object? obj) => Equals(obj as FormModel);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Command, StringComparer.Ordinal);
        foreach (var arg in Args) { hash.Add(arg, StringComparer.Ordinal); }
        hash.Add(DictionaryEquality.Hash(Env));
        hash.Add(DictionaryEquality.Hash(Additional));
        return hash.ToHashCode();
    }
}
