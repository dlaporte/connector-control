using System.Text;
using System.Text.Json;

namespace ConnectorControl.Core;

public static class ClaudeConfigIO
{
    /// <summary>Why a config file is refused when it names one key twice; it reads inside the backup refusal's
    /// parenthesis as well as on its own.</summary>
    public const string RepeatedKeyError = "the same name appears more than once";

    /// <summary>Why a config file is refused when it does not parse: fixed words, alike on both platforms, that quote
    /// nothing from the file, as the reader's own message would. It reads inside the backup refusal's parenthesis as well.</summary>
    public const string NotJsonError = "the text does not parse as JSON";

    private static readonly IReadOnlyDictionary<string, JsonValue> Empty =
        new Dictionary<string, JsonValue>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, JsonValue> ReadMcpServers(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }
        return McpServers(File.ReadAllBytes(path));
    }

    /// <summary>The servers a config file's bytes hold, parsed as <see cref="ReadMcpServers"/> parses the file,
    /// for a caller that has already read them and must use exactly what it read.</summary>
    public static IReadOnlyDictionary<string, JsonValue> McpServers(byte[] data)
    {
        var root = ParseRoot(data);
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
        AtomicFile.Write(Serialized(updated), path);
    }

    /// <summary>The bytes this app writes for a config's top-level object, whether it changed the servers or
    /// is putting a backup back: one format, so a written file never depends on which did it.</summary>
    public static byte[] Serialized(JsonValue root) => AppleJsonWriter.WriteUtf8(root, AppleJsonFormat.Serialization);

    /// <summary>
    /// Refuses a config whose top level, or whose mcpServers object, names one key twice - exactly, or
    /// in two spellings Unicode counts as the same text, such as an é composed and decomposed. This
    /// app's parser keeps one of them and Claude's may keep the other, so a file put back as it stands
    /// could run a server the question never listed. It reads the raw bytes, since a parser has
    /// already dropped the repeat. A repeat any deeper is written back as it was parsed, so what
    /// Claude reads there is what this app read. <paramref name="data"/> is bytes <see cref="ParseRoot"/>
    /// has accepted.
    ///
    /// Mirror: Sources/ConnectorControlCore/ClaudeConfigIO.swift
    /// </summary>
    public static void RequireUniqueKeys(byte[] data)
    {
        ReadOnlySpan<byte> bytes = data;
        // JsonValue.Parse accepts a byte-order mark, so ParseRoot has let one through.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }
        // No bytes at all are an empty file to ParseRoot, and an error to the reader.
        if (bytes.IsEmpty)
        {
            return;
        }
        // Platform idiom: the reader sees every property name, where the Mac mirror scans the bytes itself.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        try
        {
            ReadUniqueKeys(ref reader);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Bytes ParseRoot would refuse, UTF-16 or UTF-32 among them, refused as it refuses them, never skipped.
            throw new ClaudeConfigException(NotJsonError);
        }
    }

    private static void ReadUniqueKeys(ref Utf8JsonReader reader)
    {
        var serversNext = false;   // the root's mcpServers key was just read; its value comes next
        var inServers = false;     // inside the root's mcpServers object
        var rootKeys = new HashSet<string>(StringComparer.Ordinal);
        var serverKeys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1 || (reader.CurrentDepth == 2 && inServers):
                {
                    // Platform idiom: composed, so two spellings of the same text are one key, as Swift's
                    // own string comparison makes them on the Mac.
                    var key = reader.GetString()!.Normalize(NormalizationForm.FormC);
                    if (reader.CurrentDepth == 1)
                    {
                        if (!rootKeys.Add(key))
                        {
                            throw new ClaudeConfigException(RepeatedKeyError);
                        }
                        serversNext = key == "mcpServers";
                    }
                    else if (!serverKeys.Add(key))
                    {
                        throw new ClaudeConfigException(RepeatedKeyError);
                    }
                    break;
                }
                case JsonTokenType.PropertyName:
                    break;
                case JsonTokenType.StartObject:
                    inServers = inServers || (serversNext && reader.CurrentDepth == 1);
                    serversNext = false;
                    break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 1:
                    inServers = false;
                    break;
                default:
                    // Any other value: not the servers' object.
                    serversNext = false;
                    break;
            }
        }
    }

    private static JsonValue? ReadRootIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return ParseRoot(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Parses raw file bytes into the top-level JSON object a Claude config or one of its backups
    /// must be. Shared so ConfigService.RestoreClaudeConfig and this type's own read path cannot
    /// disagree on what counts as valid; a caller with its own file-specific wording catches
    /// ClaudeConfigException and rethrows with that instead of this method's generic detail.
    /// <para>
    /// A zero-byte payload (crash/truncation artifact) is deliberately treated like a missing file,
    /// not malformed JSON: there is nothing in it to preserve, and callers back up before writing.
    /// A non-object top level is the one shape this app can never work with, so that alone throws.
    /// </para>
    /// </summary>
    public static JsonValue ParseRoot(byte[] data)
    {
        if (data.Length == 0)
        {
            return JsonValue.Object();
        }
        // Platform idiom: the reader is UTF-8 only, so UTF-16 and UTF-32 fail to parse here; the Mac mirror,
        // whose JSONSerialization reads them too, refuses them alike before it parses. So too nesting past
        // 64, which JsonValue.Parse refuses and JSONSerialization would read.
        JsonValue parsed;
        try
        {
            parsed = JsonValue.Parse(data);
        }
        catch (JsonException)
        {
            throw new ClaudeConfigException(NotJsonError);
        }
        if (parsed.Kind != JsonKind.Object)
        {
            throw new ClaudeConfigException("top level is not a JSON object");
        }
        return parsed;
    }
}
