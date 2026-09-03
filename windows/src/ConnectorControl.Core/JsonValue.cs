using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace ConnectorControl.Core;

/// <summary>
/// Immutable JSON tree with structural equality — the C# twin of the Swift
/// <c>JSONValue</c> enum. Objects keep their keys ordinally sorted, which is
/// also the order Apple's JSONEncoder emits with <c>.sortedKeys</c>.
/// </summary>
public sealed class JsonValue : IEquatable<JsonValue>
{
    private static readonly ImmutableArray<JsonValue> EmptyArray = ImmutableArray<JsonValue>.Empty;
    private static readonly ImmutableSortedDictionary<string, JsonValue> EmptyObject =
        ImmutableSortedDictionary.Create<string, JsonValue>(StringComparer.Ordinal);

    private readonly bool boolValue;
    private readonly long intValue;
    private readonly double doubleValue;
    private readonly string stringValue;
    private readonly ImmutableArray<JsonValue> arrayItems;
    private readonly ImmutableSortedDictionary<string, JsonValue> objectProperties;

    private JsonValue(
        JsonKind kind, bool b, long i, double d, string s,
        ImmutableArray<JsonValue> array, ImmutableSortedDictionary<string, JsonValue> obj)
    {
        Kind = kind;
        boolValue = b;
        intValue = i;
        doubleValue = d;
        stringValue = s;
        arrayItems = array;
        objectProperties = obj;
    }

    public JsonKind Kind { get; }

    public static readonly JsonValue Null = new(JsonKind.Null, false, 0, 0, "", EmptyArray, EmptyObject);

    public static JsonValue Bool(bool value) => new(JsonKind.Bool, value, 0, 0, "", EmptyArray, EmptyObject);

    public static JsonValue Int(long value) => new(JsonKind.Int, false, value, 0, "", EmptyArray, EmptyObject);

    public static JsonValue Double(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException("JSON cannot represent NaN or infinity.", nameof(value));
        }
        return new(JsonKind.Double, false, 0, value, "", EmptyArray, EmptyObject);
    }

    public static JsonValue String(string value) =>
        new(JsonKind.String, false, 0, 0, value ?? throw new ArgumentNullException(nameof(value)), EmptyArray, EmptyObject);

    public static JsonValue Array(IEnumerable<JsonValue> items) =>
        new(JsonKind.Array, false, 0, 0, "", items.ToImmutableArray(), EmptyObject);

    public static JsonValue Object(IEnumerable<KeyValuePair<string, JsonValue>> properties)
    {
        var builder = EmptyObject.ToBuilder();
        foreach (var (key, value) in properties)
        {
            builder[key] = value;
        }
        return new(JsonKind.Object, false, 0, 0, "", EmptyArray, builder.ToImmutable());
    }

    public static JsonValue Object(params (string Key, JsonValue Value)[] properties) =>
        Object(properties.Select(p => new KeyValuePair<string, JsonValue>(p.Key, p.Value)));

    public bool BoolValue => Kind == JsonKind.Bool ? boolValue : throw Mismatch(JsonKind.Bool);
    public long IntValue => Kind == JsonKind.Int ? intValue : throw Mismatch(JsonKind.Int);
    public double DoubleValue => Kind == JsonKind.Double ? doubleValue : throw Mismatch(JsonKind.Double);
    public string StringValue => Kind == JsonKind.String ? stringValue : throw Mismatch(JsonKind.String);
    public ImmutableArray<JsonValue> ArrayItems => Kind == JsonKind.Array ? arrayItems : throw Mismatch(JsonKind.Array);
    public ImmutableSortedDictionary<string, JsonValue> ObjectProperties =>
        Kind == JsonKind.Object ? objectProperties : throw Mismatch(JsonKind.Object);

    private InvalidOperationException Mismatch(JsonKind wanted) =>
        new($"JsonValue is {Kind}, not {wanted}.");

    /// <summary>Property lookup on an object; null when absent or when this is not an object.</summary>
    public JsonValue? this[string key] =>
        Kind == JsonKind.Object && objectProperties.TryGetValue(key, out var v) ? v : null;

    /// <summary>Copy of this object with <paramref name="key"/> set. Throws when not an object.</summary>
    public JsonValue With(string key, JsonValue value) =>
        new(JsonKind.Object, false, 0, 0, "", EmptyArray, ObjectProperties.SetItem(key, value));

    /// <summary>Copy of this object without <paramref name="key"/>. Throws when not an object.</summary>
    public JsonValue Without(string key) =>
        new(JsonKind.Object, false, 0, 0, "", EmptyArray, ObjectProperties.Remove(key));

    /// <summary>Swift <c>typeName</c>: the wording the editor's loss warnings use.</summary>
    public string TypeName => Kind switch
    {
        JsonKind.Null => "null",
        JsonKind.Bool => "boolean",
        JsonKind.Int or JsonKind.Double => "number",
        JsonKind.String => "string",
        JsonKind.Array => "array",
        JsonKind.Object => "object",
        _ => throw new InvalidOperationException(),
    };

    // MARK: output (Task 6)

    /// <summary>Swift <c>serialized()</c>: Apple JSONEncoder pretty + sorted keys, slashes escaped.</summary>
    public byte[] Serialize() => AppleJsonWriter.WriteUtf8(this, AppleJsonFormat.Encoder);

    /// <summary>Swift <c>editorText()</c>: same as <see cref="Serialize"/> without escaping slashes.</summary>
    public string EditorText() => AppleJsonWriter.Write(this, AppleJsonFormat.EditorText);

    // MARK: parsing

    public static JsonValue Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    /// <summary>Strict parse (no comments, no trailing commas). Throws <see cref="JsonException"/>.</summary>
    public static JsonValue Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            if (!reader.Read())
            {
                throw new JsonException("The document is empty.");
            }
            var value = ReadValue(ref reader);
            if (reader.Read())
            {
                throw new JsonException("Unexpected content after the JSON value.");
            }
            return value;
        }
        catch (JsonException ex) when (ex.GetType().Name == "JsonReaderException")
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    private static JsonValue ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return Null;
            case JsonTokenType.True:
                return Bool(true);
            case JsonTokenType.False:
                return Bool(false);
            case JsonTokenType.String:
                return String(reader.GetString()!);
            case JsonTokenType.Number:
                return ReadNumber(ref reader);
            case JsonTokenType.StartArray:
            {
                var items = ImmutableArray.CreateBuilder<JsonValue>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(ReadValue(ref reader));
                }
                return Array(items);
            }
            case JsonTokenType.StartObject:
            {
                var builder = EmptyObject.ToBuilder();
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var key = reader.GetString()!;
                    if (!reader.Read())
                    {
                        throw new JsonException("Property without a value.");
                    }
                    builder[key] = ReadValue(ref reader);   // last duplicate wins
                }
                return new(JsonKind.Object, false, 0, 0, "", EmptyArray, builder.ToImmutable());
            }
            default:
                throw new JsonException($"Unexpected token {reader.TokenType}.");
        }
    }

    private static JsonValue ReadNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var l))
        {
            return Int(l);
        }
        double d;
        try
        {
            d = reader.GetDouble();
        }
        catch (FormatException ex)
        {
            throw new JsonException("Number is out of range for a double.", ex);
        }
        if (double.IsInfinity(d) || double.IsNaN(d))
        {
            throw new JsonException("Number is out of range for a double.");
        }
        // Apple's JSONDecoder decodes 2.0, 1e2 and 100.000 as Int; mirror it so
        // re-parsing our own output never flips a value between kinds.
        if (Math.Floor(d) == d && d >= -9223372036854775808.0 && d < 9223372036854775808.0)
        {
            return Int((long)d);
        }
        return Double(d);
    }

    // MARK: equality

    public bool Equals(JsonValue? other)
    {
        if (other is null || Kind != other.Kind)
        {
            return false;
        }
        switch (Kind)
        {
            case JsonKind.Null:
                return true;
            case JsonKind.Bool:
                return boolValue == other.boolValue;
            case JsonKind.Int:
                return intValue == other.intValue;
            case JsonKind.Double:
                return doubleValue.Equals(other.doubleValue);
            case JsonKind.String:
                return string.Equals(stringValue, other.stringValue, StringComparison.Ordinal);
            case JsonKind.Array:
                if (arrayItems.Length != other.arrayItems.Length)
                {
                    return false;
                }
                for (int i = 0; i < arrayItems.Length; i++)
                {
                    if (!arrayItems[i].Equals(other.arrayItems[i]))
                    {
                        return false;
                    }
                }
                return true;
            case JsonKind.Object:
                if (objectProperties.Count != other.objectProperties.Count)
                {
                    return false;
                }
                foreach (var (key, value) in objectProperties)
                {
                    if (!other.objectProperties.TryGetValue(key, out var otherValue) || !value.Equals(otherValue))
                    {
                        return false;
                    }
                }
                return true;
            default:
                return false;
        }
    }

    public override bool Equals(object? obj) => obj is JsonValue other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        switch (Kind)
        {
            case JsonKind.Bool: hash.Add(boolValue); break;
            case JsonKind.Int: hash.Add(intValue); break;
            case JsonKind.Double: hash.Add(doubleValue); break;
            case JsonKind.String: hash.Add(stringValue, StringComparer.Ordinal); break;
            case JsonKind.Array:
                foreach (var item in arrayItems) { hash.Add(item); }
                break;
            case JsonKind.Object:
                foreach (var (key, value) in objectProperties)
                {
                    hash.Add(key, StringComparer.Ordinal);
                    hash.Add(value);
                }
                break;
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(JsonValue? left, JsonValue? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(JsonValue? left, JsonValue? right) => !(left == right);

    public override string ToString() => $"JsonValue({Kind})";
}
