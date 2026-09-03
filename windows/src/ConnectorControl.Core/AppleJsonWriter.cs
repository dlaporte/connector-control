using System.Globalization;
using System.Text;

namespace ConnectorControl.Core;

/// <summary>Writes a <see cref="JsonValue"/> in one of Apple Foundation's output formats, byte for byte.</summary>
public static class AppleJsonWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Write(JsonValue value, AppleJsonFormat format)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, format, depth: 0);
        return sb.ToString();
    }

    public static byte[] WriteUtf8(JsonValue value, AppleJsonFormat format) => Utf8NoBom.GetBytes(Write(value, format));

    private static void WriteValue(StringBuilder sb, JsonValue value, AppleJsonFormat format, int depth)
    {
        switch (value.Kind)
        {
            case JsonKind.Null:
                sb.Append("null");
                break;
            case JsonKind.Bool:
                sb.Append(value.BoolValue ? "true" : "false");
                break;
            case JsonKind.Int:
                sb.Append(value.IntValue.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonKind.Double:
                sb.Append(AppleDoubleFormatter.Format(value.DoubleValue, format.NumberStyle));
                break;
            case JsonKind.String:
                WriteString(sb, value.StringValue, format.EscapeSlashes);
                break;
            case JsonKind.Array:
            {
                var items = value.ArrayItems;
                WriteContainer(sb, '[', ']', items.Length, format, depth,
                    i => WriteValue(sb, items[i], format, depth + 1));
                break;
            }
            case JsonKind.Object:
            {
                var props = value.ObjectProperties;
                var keys = format.KeyOrder == JsonKeyOrder.Ordinal
                    ? props.Keys.ToArray()                                        // already ordinal
                    : props.Keys.OrderBy(k => k, AppleKeyCollation.Comparer).ToArray();
                WriteContainer(sb, '{', '}', keys.Length, format, depth, i =>
                {
                    WriteString(sb, keys[i], format.EscapeSlashes);
                    sb.Append(format.Pretty ? " : " : ":");
                    WriteValue(sb, props[keys[i]], format, depth + 1);
                });
                break;
            }
        }
    }

    private static void WriteContainer(StringBuilder sb, char open, char close, int count, AppleJsonFormat format, int depth, Action<int> writeItem)
    {
        sb.Append(open);
        if (!format.Pretty)
        {
            for (int i = 0; i < count; i++)
            {
                if (i > 0) { sb.Append(','); }
                writeItem(i);
            }
            sb.Append(close);
            return;
        }
        sb.Append('\n');
        if (count == 0)
        {
            sb.Append('\n');   // Apple prints a blank line inside an empty container
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                if (i > 0) { sb.Append(",\n"); }
                Indent(sb, depth + 1);
                writeItem(i);
            }
            sb.Append('\n');
        }
        Indent(sb, depth);
        sb.Append(close);
    }

    private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

    private static void WriteString(StringBuilder sb, string s, bool escapeSlashes)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '/': sb.Append(escapeSlashes ? "\\/" : "/"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
