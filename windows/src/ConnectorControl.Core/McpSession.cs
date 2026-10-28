using System.Text;
using System.Text.Json;

namespace ConnectorControl.Core;

/// <summary>
/// Mirror: Sources/ConnectorControlCore/McpSession.swift
///
/// The part of the Model Context Protocol Connector Control serves, as newline-delimited JSON-RPC
/// 2.0: the handshake with protocol-version negotiation, <c>ping</c>, <c>tools/list</c>,
/// <c>tools/call</c> with progress, and cancellation. Lines in through <see cref="Receive"/>, lines
/// out through <c>send</c>, no I/O of its own, one session per connection. UI-thread only (the
/// Swift side is <c>@MainActor</c>): the transport hands each line over on the UI thread, where the
/// tools run.
/// </summary>
public sealed class McpSession(IMcpToolHost host, string version, Action<string> send)
{
    public const string ServerName = "connector-control";
    /// <summary>Newest first; the first is what a client asking for anything else is offered.</summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    /// <summary>The largest message read; anything longer is refused without being parsed.</summary>
    public const int MaxMessageBytes = 1_048_576;
    public const int ParseErrorCode = -32700;
    public const int InvalidRequestCode = -32600;
    public const int MethodNotFoundCode = -32601;
    public const int InvalidParamsCode = -32602;
    public const int NotInitializedCode = -32002;
    public const string ParseErrorMessage = "Parse error";
    public const string InvalidRequestMessage = "Invalid Request";
    public const string MethodNotFoundMessage = "Method not found";
    public const string InvalidParamsMessage = "Invalid params";
    public const string NotInitializedMessage = "Server not initialized";
    public const string TooLargeMessage = "Message too large";

    public static string UnknownToolMessage(string name) => $"Unknown tool: {name}";

    /// <summary>The client's own name from <c>initialize</c>, which the gate names as the asker.</summary>
    public string? ClientName { get; private set; }
    public bool IsInitialized { get; private set; }

    /// <summary>Requests still running, by id.</summary>
    private readonly HashSet<JsonValue> running = [];
    /// <summary>Running requests the client has cancelled: their progress and their answer are dropped.</summary>
    private readonly HashSet<JsonValue> cancelled = [];

    public void Receive(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
        {
            Reply(InvalidRequestCode, TooLargeMessage, JsonValue.Null);
            return;
        }
        var trimmed = line.Trim();
        if (trimmed.Length == 0) { return; }
        JsonValue message;
        try
        {
            message = JsonValue.Parse(trimmed);
        }
        catch (JsonException)
        {
            Reply(ParseErrorCode, ParseErrorMessage, JsonValue.Null);
            return;
        }
        if (message.Kind != JsonKind.Object || message["jsonrpc"] != JsonValue.String("2.0"))
        {
            Reply(InvalidRequestCode, InvalidRequestMessage, RequestId(message) ?? JsonValue.Null);
            return;
        }
        if (message["method"] is not { Kind: JsonKind.String } methodValue)
        {
            // An answer to something this server never asks is not worth an error of its own.
            if (message["id"] is not null && (message["result"] is not null || message["error"] is not null)) { return; }
            Reply(InvalidRequestCode, InvalidRequestMessage, RequestId(message) ?? JsonValue.Null);
            return;
        }
        var method = methodValue.StringValue;
        var parameters = message["params"] ?? JsonValue.Object();
        if (message["id"] is not { } rawId)
        {
            Notification(method, parameters);
            return;
        }
        if (ValidId(rawId) is not { } id)
        {
            Reply(InvalidRequestCode, InvalidRequestMessage, JsonValue.Null);
            return;
        }
        Request(method, parameters, id);
    }

    // MARK: Requests

    private void Request(string method, JsonValue parameters, JsonValue id)
    {
        switch (method)
        {
            case "initialize":
                Initialize(parameters, id);
                break;
            case "ping":
                Respond(id, JsonValue.Object());
                break;
            case not null when !IsInitialized:
                Reply(NotInitializedCode, NotInitializedMessage, id);
                break;
            case "tools/list":
                Respond(id, JsonValue.Object(("tools", JsonValue.Array(host.ListTools()))));
                break;
            case "tools/call":
                CallTool(parameters, id);
                break;
            default:
                Reply(MethodNotFoundCode, MethodNotFoundMessage, id);
                break;
        }
    }

    private void Initialize(JsonValue parameters, JsonValue id)
    {
        if (parameters.Kind != JsonKind.Object)
        {
            Reply(InvalidParamsCode, InvalidParamsMessage, id);
            return;
        }
        var negotiated = SupportedProtocolVersions[0];
        if (parameters["protocolVersion"] is { Kind: JsonKind.String } asked && SupportedProtocolVersions.Contains(asked.StringValue))
        {
            negotiated = asked.StringValue;
        }
        if (parameters["clientInfo"] is { Kind: JsonKind.Object } info && info["name"] is { Kind: JsonKind.String } name)
        {
            ClientName = name.StringValue;
        }
        IsInitialized = true;
        Respond(id, JsonValue.Object(
            ("protocolVersion", JsonValue.String(negotiated)),
            ("capabilities", JsonValue.Object(("tools", JsonValue.Object()))),
            ("serverInfo", JsonValue.Object(("name", JsonValue.String(ServerName)), ("version", JsonValue.String(version))))));
    }

    private void CallTool(JsonValue parameters, JsonValue id)
    {
        if (parameters.Kind != JsonKind.Object || parameters["name"] is not { Kind: JsonKind.String } nameValue)
        {
            Reply(InvalidParamsCode, InvalidParamsMessage, id);
            return;
        }
        var name = nameValue.StringValue;
        var arguments = parameters["arguments"] ?? JsonValue.Object();
        if (arguments.Kind != JsonKind.Object)
        {
            Reply(InvalidParamsCode, InvalidParamsMessage, id);
            return;
        }
        if (!host.ListTools().Any(tool => ToolName(tool) == name))
        {
            Reply(InvalidParamsCode, UnknownToolMessage(name), id);
            return;
        }
        JsonValue? token = null;
        if (parameters["_meta"] is { Kind: JsonKind.Object } meta && meta["progressToken"] is { } given && ValidId(given) is not null)
        {
            token = given;
        }
        running.Add(id);
        // The Swift side captures self weakly; the GC has no cycle to break here.
        host.CallTool(name, arguments, token, value =>
        {
            if (token is null || !running.Contains(id) || cancelled.Contains(id)) { return; }
            Write(JsonValue.Object(
                ("jsonrpc", JsonValue.String("2.0")),
                ("method", JsonValue.String("notifications/progress")),
                ("params", JsonValue.Object(("progressToken", token), ("progress", Number(value))))));
        }, result =>
        {
            if (!running.Remove(id)) { return; }
            if (cancelled.Remove(id)) { return; }
            Respond(id, result);
        });
    }

    // MARK: Notifications

    /// <summary>
    /// <c>notifications/initialized</c> needs nothing; a cancellation marks a running request so its
    /// answer is dropped. Everything else is ignored, as a notification is never answered.
    /// </summary>
    private void Notification(string method, JsonValue parameters)
    {
        if (method != "notifications/cancelled" || parameters.Kind != JsonKind.Object
            || parameters["requestId"] is not { } given || ValidId(given) is not { } id || !running.Contains(id))
        {
            return;
        }
        cancelled.Add(id);
    }

    // MARK: Writing

    private void Respond(JsonValue id, JsonValue result) =>
        Write(JsonValue.Object(("jsonrpc", JsonValue.String("2.0")), ("id", id), ("result", result)));

    private void Reply(int code, string message, JsonValue id) =>
        Write(JsonValue.Object(("jsonrpc", JsonValue.String("2.0")), ("id", id),
            ("error", JsonValue.Object(("code", JsonValue.Int(code)), ("message", JsonValue.String(message))))));

    private void Write(JsonValue message) => send(message.CompactLine());

    /// <summary>A JSON-RPC id this server accepts: a string or an integer.</summary>
    internal static JsonValue? ValidId(JsonValue value) => value.Kind switch
    {
        JsonKind.String or JsonKind.Int => value,
        _ => null,
    };

    /// <summary>The id of a message too broken to answer properly, when it has a usable one.</summary>
    private static JsonValue? RequestId(JsonValue message) =>
        message.Kind == JsonKind.Object && message["id"] is { } id ? ValidId(id) : null;

    internal static string? ToolName(JsonValue descriptor) =>
        descriptor.Kind == JsonKind.Object && descriptor["name"] is { Kind: JsonKind.String } name ? name.StringValue : null;

    /// <summary>Whole numbers go out as integers, which both platforms write the same way.</summary>
    internal static JsonValue Number(double value) =>
        value == Math.Round(value) && Math.Abs(value) < 1e15 ? JsonValue.Int((long)value) : JsonValue.Double(value);
}
