using System.Text.Json;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlRelay.swift
///
/// <c>connector-control mcp</c>, the relay Claude Desktop runs, as a state machine: lines from Claude, lines
/// from the app and the connection's comings and goings in; what to send where out. Every line passes
/// as it came. The relay keeps only what it needs to outlive the app restarting: Claude's
/// <c>initialize</c> and <c>notifications/initialized</c>, replayed to a new connection with the replayed
/// answer dropped, and the requests still open, which a dropped connection answers "restarted". It
/// reconnects only when Claude next sends something. No I/O: <see cref="ControlRelayRunner"/> does that.
/// The Mac mirror's actions are an enum with payloads; here each is a record.
/// </summary>
public sealed class ControlRelay
{
    public const string RestartedError = "Connector Control restarted — try again.";
    /// <summary>The replayed <c>initialize</c> goes out under an id of this form, which Claude never uses, so
    /// its answer is known and dropped.</summary>
    public const string ReplayIdPrefix = "connector-control-replay-";

    public abstract record Action
    {
        private Action()
        {
        }

        public sealed record ToApp(string Line) : Action;

        public sealed record ToClaude(string Line) : Action;

        /// <summary>Connect — starting the app where that is allowed — and report with
        /// <see cref="ConnectSucceeded"/> or <see cref="ConnectFailed"/>.</summary>
        public sealed record Connect : Action;

        public sealed record Exit(int Code) : Action;
    }

    public bool IsConnected { get; private set; }
    /// <summary>Claude's <c>initialize</c>, as it sent it.</summary>
    private JsonValue? initialize;
    /// <summary>Claude's <c>notifications/initialized</c>, as it sent it.</summary>
    private string? initialized;
    /// <summary>The app has answered Claude's own <c>initialize</c>.</summary>
    private bool handshakeDone;
    /// <summary>Requests forwarded and not yet answered, oldest first.</summary>
    private readonly List<(JsonValue Id, string Method)> pending = [];
    /// <summary>Lines from Claude waiting for a connection.</summary>
    private readonly List<string> held = [];
    private JsonValue? replayId;
    private int replays;
    /// <summary>Claude has closed its side: the relay ends once nothing it forwarded is left unanswered.</summary>
    private bool closing;

    public IReadOnlyList<Action> FromClaude(string line)
    {
        var message = Message(line);
        if (message is not null && message["id"] is not null && message["method"] == JsonValue.String("initialize") && initialize is null)
        {
            initialize = message;
        }
        if (message is not null && message["id"] is null && message["method"] == JsonValue.String("notifications/initialized") && initialized is null)
        {
            initialized = line;
        }
        // A cancelled request is never answered — the session drops its answer — so it is waited for
        // no longer.
        if (message is not null && message["id"] is null && message["method"] == JsonValue.String("notifications/cancelled")
            && message["params"] is { Kind: JsonKind.Object } parameters && parameters["requestId"] is { } id)
        {
            pending.RemoveAll(entry => entry.Id == id);
        }
        if (!IsConnected)
        {
            held.Add(line);
            // After the first handshake only a request reconnects, and may start the app: a
            // notification, or a line that is no message at all, waits for the next request.
            return handshakeDone && message?["id"] is null ? [] : [new Action.Connect()];
        }
        var actions = Forward(line, message);
        if (closing && pending.Count == 0)
        {
            actions.Add(new Action.Exit(ControlOutput.DoneExit));
        }
        return actions;
    }

    public IReadOnlyList<Action> ConnectSucceeded()
    {
        IsConnected = true;
        var actions = new List<Action>();
        if (handshakeDone && initialize is { } replay)
        {
            replays++;
            var id = JsonValue.String(ReplayIdPrefix + replays);
            replayId = id;
            actions.Add(new Action.ToApp(replay.With("id", id).CompactLine()));
            if (initialized is not null)
            {
                actions.Add(new Action.ToApp(initialized));
            }
        }
        var lines = held.ToList();
        held.Clear();
        foreach (var line in lines)
        {
            actions.AddRange(Forward(line, Message(line)));
        }
        return actions;
    }

    /// <summary>No connection could be made: each request held for it is answered with
    /// <paramref name="reason"/>. Before the first handshake there is nothing to keep running for, and the
    /// relay ends.</summary>
    public IReadOnlyList<Action> ConnectFailed(string reason)
    {
        var lines = held.ToList();
        held.Clear();
        var actions = new List<Action>();
        foreach (var line in lines)
        {
            if (Message(line) is { } message && Failure(message, reason) is { } answer)
            {
                actions.Add(new Action.ToClaude(answer));
            }
        }
        if (!handshakeDone)
        {
            actions.Add(new Action.Exit(ControlOutput.UnreachableExit));
        }
        else if (closing)
        {
            actions.Add(new Action.Exit(ControlOutput.DoneExit));
        }
        return actions;
    }

    public IReadOnlyList<Action> FromApp(string line)
    {
        var message = Message(line);
        if (message is not null && message["method"] is null && message["id"] is { } id)
        {
            if (id == replayId)
            {
                replayId = null;
                return [];
            }
            var index = pending.FindIndex(entry => entry.Id == id);
            if (index >= 0)
            {
                var method = pending[index].Method;
                pending.RemoveAt(index);
                if (method == "initialize")
                {
                    handshakeDone = true;
                }
            }
        }
        var actions = new List<Action> { new Action.ToClaude(line) };
        if (closing && pending.Count == 0)
        {
            actions.Add(new Action.Exit(ControlOutput.DoneExit));
        }
        return actions;
    }

    /// <summary>The connection to the app has gone: each request it held is answered "restarted". Before the
    /// app answered Claude's own <c>initialize</c>, the relay ends.</summary>
    public IReadOnlyList<Action> ConnectionLost()
    {
        if (!IsConnected)
        {
            return [];
        }
        IsConnected = false;
        replayId = null;
        var waiting = pending.ToList();
        pending.Clear();
        var actions = waiting.Select(entry => (Action)new Action.ToClaude(Failure(entry.Id, entry.Method, RestartedError))).ToList();
        if (!handshakeDone)
        {
            actions.Add(new Action.Exit(ControlOutput.UnreachableExit));
        }
        else if (closing)
        {
            actions.Add(new Action.Exit(ControlOutput.DoneExit));
        }
        return actions;
    }

    /// <summary>Claude closed its side: the relay ends now, or once what it forwarded is answered.</summary>
    public IReadOnlyList<Action> ClaudeClosed()
    {
        closing = true;
        return pending.Count == 0 || !IsConnected ? [new Action.Exit(ControlOutput.DoneExit)] : [];
    }

    /// <summary>Passes a line on. A request is waited for only when the session will answer it under its own
    /// id: one whose id is neither a string nor an integer, or is already running, is answered with id null
    /// or not at all.</summary>
    private List<Action> Forward(string line, JsonValue? message)
    {
        if (message is not null && message["method"] is { Kind: JsonKind.String } method && message["id"] is { } raw
            && McpSession.ValidId(raw) is { } id && !pending.Exists(entry => entry.Id == id))
        {
            pending.Add((id, method.StringValue));
        }
        return [new Action.ToApp(line)];
    }

    /// <summary>The object a line holds, read only to know what the line is — never to change it.</summary>
    internal static JsonValue? Message(string line)
    {
        if (!McpSession.Nests(McpSession.MaxNestingDepth, line))
        {
            return null;
        }
        JsonValue fields;
        try
        {
            fields = JsonValue.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
        return fields.Kind == JsonKind.Object ? fields : null;
    }

    internal static string? Failure(JsonValue message, string reason) =>
        message["id"] is { } id && message["method"] is { Kind: JsonKind.String } method ? Failure(id, method.StringValue, reason) : null;

    /// <summary>What a request gets when the app cannot answer it: a tool call a refusal, read as Claude reads
    /// any other; anything else a JSON-RPC error.</summary>
    internal static string Failure(JsonValue id, string method, string reason)
    {
        if (method != "tools/call")
        {
            return McpSession.ErrorLine(McpSession.InternalErrorCode, reason, id);
        }
        return JsonValue.Object(("jsonrpc", JsonValue.String("2.0")), ("id", id), ("result", ControlResult.Refusal(reason).McpResult)).CompactLine();
    }
}
