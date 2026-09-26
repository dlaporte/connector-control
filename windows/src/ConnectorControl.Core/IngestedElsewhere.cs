namespace ConnectorControl.Core;

/// <summary>
/// Connectors a load took out of Claude's file into a collection other than the active one,
/// because the active one is subscribed (<see cref="ConfigService.IngestTarget"/>).
/// </summary>
public sealed record IngestedElsewhere(string Collection, IReadOnlyList<string> Names)
{
    public bool Equals(IngestedElsewhere? other) =>
        other is not null && Collection == other.Collection && Names.SequenceEqual(other.Names);

    public override int GetHashCode() => HashCode.Combine(Collection, Names.Count);
}
