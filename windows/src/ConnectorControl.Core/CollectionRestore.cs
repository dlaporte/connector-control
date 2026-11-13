namespace ConnectorControl.Core;

/// <summary>
/// What putting a collection back exactly as a backup holds it changes, connector by connector.
/// Names compare as the store compares them (<see cref="Collection.NameComparer"/>): a connector held
/// under another spelling of its name is the same connector. A connector that comes back or changes is
/// named as the backup spells it, one that goes as the store does.
///
/// Mirror: Sources/ConnectorControlCore/CollectionRestore.swift
/// </summary>
public sealed class CollectionRestore : IEquatable<CollectionRestore>
{
    public abstract record Change
    {
        private Change()
        {
        }

        /// <summary>Not held now: the restore brings it back, on or off.</summary>
        public sealed record ComesBack(bool On) : Change;

        /// <summary>Held now with another config or switch: on or off after the restore.</summary>
        public sealed record Changes(bool On) : Change;

        /// <summary>Held now and not in the backup: the restore removes it.</summary>
        public sealed record Goes : Change;
    }

    private CollectionRestore(IReadOnlyDictionary<string, Change> byName, int unchanged)
    {
        ByName = byName;
        Unchanged = unchanged;
    }

    /// <summary>Every connector the restore brings back, changes or removes, by name.</summary>
    public IReadOnlyDictionary<string, Change> ByName { get; }

    /// <summary>How many connectors the restore leaves as they are.</summary>
    public int Unchanged { get; }

    /// <summary>True when the collection is already as the backup holds it.</summary>
    public bool IsEmpty => ByName.Count == 0;

    /// <summary>
    /// <paramref name="restored"/> against <paramref name="current"/>, null for a collection that is
    /// gone. A connector is unchanged when its config and its switch are: which view its editor last
    /// showed is not something a restore is asked to put back, so it alone never makes a collection differ.
    /// </summary>
    public static CollectionRestore Changes(Collection? current, Collection restored)
    {
        var held = current?.Mcps ?? new Collection().Mcps;
        var byName = new Dictionary<string, Change>(Collection.NameComparer);
        var unchanged = 0;
        foreach (var (name, entry) in restored.Mcps)
        {
            if (held.TryGetValue(name, out var now))
            {
                if (now.Config.Equals(entry.Config) && now.Enabled == entry.Enabled)
                {
                    unchanged++;
                }
                else
                {
                    byName[name] = new Change.Changes(entry.Enabled);
                }
            }
            else
            {
                byName[name] = new Change.ComesBack(entry.Enabled);
            }
        }
        foreach (var name in held.Keys.Where(name => !restored.Mcps.ContainsKey(name)))
        {
            byName[name] = new Change.Goes();
        }
        return new CollectionRestore(byName, unchanged);
    }

    public bool Equals(CollectionRestore? other) =>
        other is not null && Unchanged == other.Unchanged && DictionaryEquality.Equal(ByName, other.ByName);

    public override bool Equals(object? obj) => Equals(obj as CollectionRestore);

    public override int GetHashCode() => HashCode.Combine(Unchanged, ByName.Count);
}
