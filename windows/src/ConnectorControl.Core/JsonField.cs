namespace ConnectorControl.Core;

/// <summary>
/// Mirror: Sources/ConnectorControlCore/JSONField.swift
/// The field checks a decoder of our own files runs: one copy of the logic and the wording, shared
/// by <see cref="CollectionDocument"/>, <see cref="CollectionsFile"/> and
/// <see cref="CollectionsLocalCache"/>. Every failure names the key it read, through
/// <paramref name="malformed"/>, which makes the reading file's own exception.
/// </summary>
internal static class JsonField
{
    internal static string RequiredString(JsonValue? value, string what, Func<string, Exception> malformed)
    {
        if (value is null)
        {
            throw malformed($"{what} is missing");
        }
        if (value.Kind != JsonKind.String)
        {
            throw malformed($"{what} is not a string");
        }
        return value.StringValue;
    }

    internal static string? OptionalString(JsonValue? value, string what, Func<string, Exception> malformed)
    {
        if (value is null || value.Kind == JsonKind.Null)
        {
            return null;
        }
        if (value.Kind != JsonKind.String)
        {
            throw malformed($"{what} is not a string");
        }
        return value.StringValue;
    }

    internal static IReadOnlyDictionary<string, JsonValue> ObjectValue(JsonValue? value, string what, Func<string, Exception> malformed)
    {
        if (value is null)
        {
            return new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        }
        if (value.Kind != JsonKind.Object)
        {
            throw malformed($"{what} is not a JSON object");
        }
        return value.ObjectProperties;
    }
}
