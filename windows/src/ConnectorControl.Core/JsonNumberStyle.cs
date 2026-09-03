namespace ConnectorControl.Core;

public enum JsonNumberStyle
{
    /// <summary>Swift's shortest round-trip <c>description</c> (JSONEncoder).</summary>
    Shortest,
    /// <summary>C <c>%.17g</c> (JSONSerialization).</summary>
    G17,
}
