using System.Text.Json;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlHello.swift
///
/// The first line on every connection to the app — who is calling and which version it is,
/// <c>{"client":"cli"|"relay","version":"1.5.0"}</c>. The command-line tool is trusted as the user and
/// the relay Claude runs is gated, so only a well-formed command-line hello — an object whose
/// "client" is exactly "cli" and whose "version" is text — makes a connection the command line;
/// anything else is the relay. Other keys are ignored, so a later tool can say more without being
/// gated for it.
/// </summary>
public sealed record ControlHello(bool IsCli, string? Version)
{
    public const string CliClient = "cli";
    public const string RelayClient = "relay";
    /// <summary>What a connection that gave no usable hello is taken for: the relay, of no known version.</summary>
    public static readonly ControlHello Unknown = new(false, null);

    /// <summary>The hello <paramref name="client"/> sends, without its newline.</summary>
    public static string Line(string client, string version) =>
        JsonValue.Object(("client", JsonValue.String(client)), ("version", JsonValue.String(version))).CompactLine();

    /// <summary>The hello a connection's first line gives. It fails closed: anything not read as a
    /// well-formed hello is <see cref="Unknown"/>, the relay. The bytes are read as strictly as a
    /// message's, so one that is not UTF-8 is never decoded lossily into a hello.</summary>
    public static ControlHello Parse(byte[] bytes)
    {
        if (bytes.Length > McpSession.MaxMessageBytes || McpSession.StrictUtf8(bytes) is not { } text
            || !McpSession.Nests(McpSession.MaxNestingDepth, text))
        {
            return Unknown;
        }
        JsonValue fields;
        try
        {
            fields = JsonValue.Parse(text);
        }
        catch (JsonException)
        {
            return Unknown;
        }
        if (fields.Kind != JsonKind.Object || fields["client"] is not { Kind: JsonKind.String } client
            || fields["version"] is not { Kind: JsonKind.String } version || version.StringValue.Length == 0)
        {
            return Unknown;
        }
        return new ControlHello(client.StringValue == CliClient, version.StringValue);
    }
}
