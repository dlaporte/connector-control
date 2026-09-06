namespace ConnectorControl.Core;

/// <summary>Key ordering of a written JSON object.</summary>
public enum JsonKeyOrder
{
    /// <summary>Byte order — what Apple's JSONEncoder <c>.sortedKeys</c> does. Used for the master list.</summary>
    Ordinal,
    /// <summary>Case-insensitive, numeric-aware — what JSONSerialization <c>.sortedKeys</c> does. Used for Claude's config.</summary>
    Collated,
}
