namespace ConnectorControl.Core;

/// <summary>Which Apple Foundation writer to imitate.</summary>
public sealed record AppleJsonFormat(JsonKeyOrder KeyOrder, JsonNumberStyle NumberStyle, bool EscapeSlashes, bool Pretty)
{
    /// <summary>JSONEncoder <c>[.prettyPrinted, .sortedKeys]</c> — the master list (<c>JSONValue.serialized()</c>).</summary>
    public static readonly AppleJsonFormat Encoder = new(JsonKeyOrder.Ordinal, JsonNumberStyle.Shortest, EscapeSlashes: true, Pretty: true);

    /// <summary>JSONEncoder <c>[.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]</c> — <c>JSONValue.editorText()</c>.</summary>
    public static readonly AppleJsonFormat EditorText = Encoder with { EscapeSlashes = false };

    /// <summary>JSONSerialization <c>[.prettyPrinted, .sortedKeys]</c> — Claude's config file.</summary>
    public static readonly AppleJsonFormat Serialization = new(JsonKeyOrder.Collated, JsonNumberStyle.G17, EscapeSlashes: true, Pretty: true);

    /// <summary>JSONSerialization <c>[.sortedKeys]</c> — the compact JSON inside mcp-remote CLI arguments.</summary>
    public static readonly AppleJsonFormat SerializationCompact = Serialization with { Pretty = false };
}
