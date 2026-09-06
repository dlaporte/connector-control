using System.Text.Json;

namespace ConnectorControl.Core;

public static class ClaudeConfigIO
{
    private static readonly IReadOnlyDictionary<string, JsonValue> Empty =
        new Dictionary<string, JsonValue>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, JsonValue> ReadMcpServers(string path)
    {
        var root = ReadRootIfPresent(path);
        if (root is null)
        {
            return Empty;
        }
        var raw = root["mcpServers"];
        if (raw is null)
        {
            return Empty;
        }
        if (raw.Kind != JsonKind.Object)
        {
            throw new ClaudeConfigException("mcpServers is not a JSON object");
        }
        return raw.ObjectProperties;
    }

    /// <summary>
    /// Reads the file fresh, replaces ONLY the mcpServers key, preserves every
    /// other key by value, and writes atomically. Missing file → created.
    /// Malformed file → throws; the file is never overwritten blindly.
    /// </summary>
    public static void Write(IReadOnlyDictionary<string, JsonValue> mcpServers, string path)
    {
        var root = ReadRootIfPresent(path) ?? JsonValue.Object();
        var updated = root.With("mcpServers", JsonValue.Object(mcpServers));
        AtomicFile.Write(AppleJsonWriter.WriteUtf8(updated, AppleJsonFormat.Serialization), path);
    }

    private static JsonValue? ReadRootIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        var data = File.ReadAllBytes(path);
        // A zero-byte file (crash/truncation artifact) is treated like a missing
        // file, not malformed JSON: there is nothing in it to preserve.
        if (data.Length == 0)
        {
            return JsonValue.Object();
        }
        JsonValue parsed;
        try
        {
            parsed = JsonValue.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new ClaudeConfigException(ex.Message);
        }
        if (parsed.Kind != JsonKind.Object)
        {
            throw new ClaudeConfigException("top level is not a JSON object");
        }
        return parsed;
    }
}
