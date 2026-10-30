using System.Text;
using System.Text.Json;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlCommandRunner.swift
///
/// One command, run as an MCP client of the app: the hello, the handshake and the call go out together,
/// and the answer to the call is what the command prints. The call asks for progress, so a change waiting
/// behind a dialog can say once that it is waiting.
/// </summary>
public static class ControlCommandRunner
{
    /// <summary>The call's id; the handshake's is 1.</summary>
    public static readonly JsonValue CallId = JsonValue.Int(2);
    public const string ProgressToken = "cli";

    /// <summary>What one command sends, line by line, without newlines.</summary>
    public static IReadOnlyList<string> OpeningLines(string tool, JsonValue arguments, string version) =>
    [
        ControlHello.Line(ControlHello.CliClient, version),
        JsonValue.Object(
            ("jsonrpc", JsonValue.String("2.0")), ("id", JsonValue.Int(1)), ("method", JsonValue.String("initialize")),
            ("params", JsonValue.Object(
                ("protocolVersion", JsonValue.String(McpSession.SupportedProtocolVersions[0])),
                ("capabilities", JsonValue.Object()),
                ("clientInfo", JsonValue.Object(("name", JsonValue.String(McpSession.ServerName)), ("version", JsonValue.String(version))))))).CompactLine(),
        JsonValue.Object(("jsonrpc", JsonValue.String("2.0")), ("method", JsonValue.String("notifications/initialized"))).CompactLine(),
        JsonValue.Object(
            ("jsonrpc", JsonValue.String("2.0")), ("id", CallId), ("method", JsonValue.String("tools/call")),
            ("params", JsonValue.Object(("name", JsonValue.String(tool)), ("arguments", arguments),
                ("_meta", JsonValue.Object(("progressToken", JsonValue.String(ProgressToken))))))).CompactLine(),
    ];

    /// <summary>Connects through <paramref name="client"/>, runs the call over that connection and closes it: what
    /// the command line prints. A connection that cannot be had ends the command as unreachable, with the reason
    /// the client gave - the app is not running, could not be started, or the tool runs elevated.</summary>
    public static ControlOutput Run(string tool, JsonValue arguments, bool json, string version, ControlClient client, Action notice)
    {
        IControlStream stream;
        try
        {
            stream = client.Connect();
        }
        catch (Exception ex)
        {
            // As the Mac's catch: the reason Connect gave, else the sentence for a start that failed.
            return ControlOutput.Unreachable(ex is ControlClient.UnreachableException unreachable
                ? ControlClient.Message(unreachable.Reason)
                : ControlClient.UnreachableError);
        }
        try
        {
            return Run(tool, arguments, json, version, stream, notice);
        }
        finally
        {
            stream.Close();
        }
    }

    /// <summary>Runs the call over <paramref name="stream"/> and says what to print. <paramref name="notice"/>
    /// runs once, at the first progress report: the change is waiting its turn. An error answered with no id -
    /// a message too large - can only be the call's, the one request still open. A line past
    /// <paramref name="limit"/> can never be read whole, so the command cannot say what happened: it ends as
    /// if the app had gone.</summary>
    public static ControlOutput Run(string tool, JsonValue arguments, bool json, string version, IControlStream stream,
        Action notice, int limit = ControlClient.MaxAnswerBytes)
    {
        var opening = string.Concat(OpeningLines(tool, arguments, version).Select(line => line + "\n"));
        if (!stream.Write(Encoding.UTF8.GetBytes(opening)))
        {
            return ControlOutput.Unreachable(ControlClient.DroppedError);
        }
        var framer = new McpLineFramer(limit);
        var noticed = false;
        while (true)
        {
            var chunk = stream.Read(65_536);
            if (chunk.Length == 0)
            {
                return ControlOutput.Unreachable(ControlClient.DroppedError);
            }
            foreach (var frame in framer.Append(chunk))
            {
                if (frame is not McpFrame.Line line)
                {
                    return ControlOutput.Unreachable(ControlClient.DroppedError);
                }
                JsonValue fields;
                try
                {
                    fields = JsonValue.Parse(line.Bytes);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (fields.Kind != JsonKind.Object)
                {
                    continue;
                }
                if (fields["method"] == JsonValue.String("notifications/progress"))
                {
                    if (!noticed)
                    {
                        noticed = true;
                        notice();
                    }
                }
                else if (fields["method"] is null
                    && (fields["id"] == CallId || (fields["id"] == JsonValue.Null && fields["error"] is not null)))
                {
                    return ControlOutput.Answer(fields, json);
                }
            }
        }
    }
}
