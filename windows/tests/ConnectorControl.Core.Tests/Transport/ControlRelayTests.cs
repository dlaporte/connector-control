using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/Transport/ControlRelayTests.swift
/// </summary>
public class ControlRelayTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-ai","version":"0.1.0"}}}""";
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string InitializeAnswer = """{"id":0,"jsonrpc":"2.0","result":{"capabilities":{"tools":{}},"protocolVersion":"2025-11-25","serverInfo":{"name":"connector-control","version":"1.5.0"}}}""";

    private static string Call(int id) => Call(rawId: id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A call whose id is written as <paramref name="rawId"/> is, JSON and all.</summary>
    private static string Call(string rawId) =>
        $$$$"""{"jsonrpc":"2.0","id":{{{{rawId}}}},"method":"tools/call","params":{"name":"get_status","arguments":{}}}""";

    private static string Answer(int id) =>
        $$$$"""{"id":{{{{id}}}},"jsonrpc":"2.0","result":{"content":[],"isError":false,"structuredContent":{}}}""";

    private static ControlRelay.Action Refused(int id, string reason) => Refused(JsonValue.Int(id), reason);

    private static ControlRelay.Action Refused(JsonValue id, string reason) =>
        new ControlRelay.Action.ToClaude(JsonValue.Object(("jsonrpc", JsonValue.String("2.0")), ("id", id),
            ("result", ControlResult.Refusal(reason).McpResult)).CompactLine());

    private static ControlRelay.Action ToApp(string line) => new ControlRelay.Action.ToApp(line);

    private static ControlRelay.Action ToClaude(string line) => new ControlRelay.Action.ToClaude(line);

    private static ControlRelay.Action Connect() => new ControlRelay.Action.Connect();

    private static ControlRelay.Action Exit(int code) => new ControlRelay.Action.Exit(code);

    /// <summary>A relay through its first handshake.</summary>
    private static ControlRelay Started()
    {
        var relay = new ControlRelay();
        Assert.Equal([Connect()], relay.FromClaude(Initialize));   // nothing connects before Claude says something
        Assert.Equal([ToApp(Initialize)], relay.ConnectSucceeded());
        Assert.Equal([ToClaude(InitializeAnswer)], relay.FromApp(InitializeAnswer));
        Assert.Equal([ToApp(Initialized)], relay.FromClaude(Initialized));
        return relay;
    }

    [Fact]
    public void EveryLinePassesAsItCame()
    {
        var relay = Started();
        Assert.Equal([ToApp(Call(1))], relay.FromClaude(Call(1)));
        const string progress = """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":5,"progressToken":"t"}}""";
        Assert.Equal([ToClaude(progress)], relay.FromApp(progress));
        Assert.Equal([ToClaude(Answer(1))], relay.FromApp(Answer(1)));
        Assert.Equal([ToApp("not json")], relay.FromClaude("not json"));   // the app answers what it cannot read
    }

    [Fact]
    public void AfterTheAppGoesTheNextRequestReconnectsAndReplaysTheHandshake()
    {
        var relay = Started();
        Assert.Empty(relay.ConnectionLost());   // nothing was waiting, and nothing happens until Claude asks
        Assert.False(relay.IsConnected);
        Assert.Equal([Connect()], relay.FromClaude(Call(2)));
        var replay = relay.ConnectSucceeded();
        Assert.Equal(3, replay.Count);
        var replayed = Assert.IsType<ControlRelay.Action.ToApp>(replay[0]).Line;
        var fields = ControlRelay.Message(replayed);
        Assert.NotNull(fields);
        Assert.Equal(JsonValue.String(ControlRelay.ReplayIdPrefix + "1"), fields["id"]);   // never an id Claude uses
        Assert.Equal(ControlRelay.Message(Initialize)?["params"], fields["params"]);   // Claude's own initialize, its name included
        Assert.Equal([ToApp(Initialized), ToApp(Call(2))], replay.Skip(1));
        Assert.Empty(relay.FromApp("""{"id":"connector-control-replay-1","jsonrpc":"2.0","result":{}}"""));   // the replayed answer is dropped
        Assert.Equal([ToClaude(Answer(2))], relay.FromApp(Answer(2)));
    }

    [Fact]
    public void ARequestInFlightWhenTheAppGoesIsToldToTryAgain()
    {
        var relay = Started();
        _ = relay.FromClaude(Call(3));
        _ = relay.FromClaude("""{"jsonrpc":"2.0","id":4,"method":"tools/list"}""");
        Assert.Equal([
            Refused(3, ControlRelay.RestartedError),
            ToClaude(McpSession.ErrorLine(McpSession.InternalErrorCode, ControlRelay.RestartedError, JsonValue.Int(4))),
        ], relay.ConnectionLost());
        // held for the next connection, and no reconnect for it
        Assert.Empty(relay.FromClaude("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}"""));
    }

    /// <summary>A cancelled request is never answered - the session drops its answer - so the relay stops
    /// waiting for it: Claude closing its side ends the relay, and a lost connection answers nothing
    /// for it.</summary>
    [Fact]
    public void ACancelledRequestIsNoLongerWaitedFor()
    {
        var relay = Started();
        _ = relay.FromClaude(Call(8));
        const string cancel = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":8}}""";
        Assert.Equal([ToApp(cancel)], relay.FromClaude(cancel));
        Assert.Equal([Exit(ControlOutput.DoneExit)], relay.ClaudeClosed());
        var other = Started();
        _ = other.FromClaude(Call(9));
        _ = other.FromClaude("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9}}""");
        Assert.Empty(other.ConnectionLost());   // no stray answer for a cancelled request
    }

    /// <summary>A request the session answers with id null - an id that is neither a string nor an integer, or
    /// one already running - is not waited for under that id.</summary>
    /// <summary>An answer under a cancelled id is the answer to a request reusing it, so the request first sent under it
    /// has ended: the id is waited for again.</summary>
    [Fact]
    public void AnAnswerUnderACancelledIdLetsItBeWaitedForAgain()
    {
        var relay = Started();
        _ = relay.FromClaude(Call(8));
        _ = relay.FromClaude("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":8}}""");
        _ = relay.FromClaude(Call(8));
        Assert.Equal([ToClaude(Answer(8))], relay.FromApp(Answer(8)));
        _ = relay.FromClaude(Call(8));
        Assert.Empty(relay.ClaudeClosed());   // 8 is waited for again
        Assert.Equal([ToClaude(Answer(8)), Exit(ControlOutput.DoneExit)], relay.FromApp(Answer(8)));
    }

    /// <summary>A cancel held for a new connection behind its request settles that request when it reaches the app,
    /// not before: the app drops the request's answer, so the relay no longer waits for it and ends once Claude
    /// closes its side.</summary>
    [Fact]
    public void AHeldCancelSettlesTheRequestHeldBeforeIt()
    {
        var relay = Started();
        _ = relay.ConnectionLost();
        Assert.Equal([Connect()], relay.FromClaude(Call(5)));
        const string cancel = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5}}""";
        Assert.Empty(relay.FromClaude(cancel));
        Assert.Equal([ToApp(Call(5)), ToApp(cancel)], relay.ConnectSucceeded().Skip(2));
        Assert.Equal([Exit(ControlOutput.DoneExit)], relay.ClaudeClosed());
    }

    /// <summary>An id Claude reuses after cancelling its request may still be running in the app, which answers the
    /// new request with id null and drops the old one's answer: nothing will come under that id, so it is not
    /// waited for, and the relay still ends once Claude closes its side. A new connection is a new session, where
    /// the id is waited for again.</summary>
    [Fact]
    public void AnIdReusedAfterItsRequestWasCancelledIsNotWaitedFor()
    {
        var relay = Started();
        _ = relay.FromClaude(Call(8));
        _ = relay.FromClaude("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":8}}""");
        Assert.Equal([ToApp(Call(8))], relay.FromClaude(Call(8)));
        Assert.Equal([Exit(ControlOutput.DoneExit)], relay.ClaudeClosed());

        var restarted = Started();
        _ = restarted.FromClaude(Call(9));
        _ = restarted.FromClaude("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9}}""");
        Assert.Empty(restarted.ConnectionLost());
        Assert.Equal([Connect()], restarted.FromClaude(Call(9)));
        _ = restarted.ConnectSucceeded();
        Assert.Empty(restarted.ClaudeClosed());   // waits for 9 in the new session
        Assert.Equal([ToClaude(Answer(9)), Exit(ControlOutput.DoneExit)], restarted.FromApp(Answer(9)));
    }

    [Fact]
    public void ARequestTheSessionCannotAnswerByItsIdIsNotWaitedFor()
    {
        var relay = Started();
        _ = relay.FromClaude("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""");
        _ = relay.FromClaude(Call(10));
        _ = relay.FromClaude(Call(10));
        Assert.Equal([Refused(10, ControlRelay.RestartedError)], relay.ConnectionLost());   // one answer for 10, none for 1.5
    }

    /// <summary>After the first handshake only a request reconnects, and may start the app: a notification, or a
    /// line that is no message, waits, held, and reaches the app after the replayed handshake.</summary>
    [Fact]
    public void OnlyARequestReconnects()
    {
        var relay = Started();
        _ = relay.ConnectionLost();
        const string cancel = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""";
        Assert.Empty(relay.FromClaude(cancel));
        Assert.Empty(relay.FromClaude(""));
        Assert.Equal([Connect()], relay.FromClaude(Call(11)));
        Assert.Equal([ToApp(Initialized), ToApp(cancel), ToApp(""), ToApp(Call(11))], relay.ConnectSucceeded().Skip(1));
    }

    /// <summary>Ids are told apart as the session reads them: 1 and 1.0 are one id, the integer 1, on both
    /// platforms; "1" is another.</summary>
    [Fact]
    public void IdsAreToldApartAsTheSessionReadsThem()
    {
        var relay = Started();
        _ = relay.FromClaude(Call(rawId: "1"));
        _ = relay.FromClaude(Call(rawId: "\"1\""));
        _ = relay.FromClaude(Call(rawId: "1.0"));
        const string answeredAsOnePointZero = """{"id":1.0,"jsonrpc":"2.0","result":{}}""";
        Assert.Equal([ToClaude(answeredAsOnePointZero)], relay.FromApp(answeredAsOnePointZero));
        // the answer under 1.0 settled 1, and nothing more was waited for under it
        Assert.Equal([Refused(JsonValue.String("1"), ControlRelay.RestartedError)], relay.ConnectionLost());
    }

    [Fact]
    public void AFailedReconnectAnswersTheRequestAndTheNextTriesAgain()
    {
        var relay = Started();
        _ = relay.ConnectionLost();
        Assert.Equal([Connect()], relay.FromClaude(Call(5)));
        Assert.Equal([Refused(5, ControlClient.UnreachableError)], relay.ConnectFailed(ControlClient.UnreachableError));
        Assert.Equal([Connect()], relay.FromClaude(Call(6)));
    }

    /// <summary>A connection that cannot be made answers what was held for it as the session would have: once for
    /// each id, and never under an id the session would not answer by.</summary>
    [Fact]
    public void AFailedReconnectAnswersEachHeldRequestOnceUnderItsOwnId()
    {
        var relay = Started();
        _ = relay.ConnectionLost();
        _ = relay.FromClaude(Call(14));
        _ = relay.FromClaude(Call(14));
        _ = relay.FromClaude("""{"jsonrpc":"2.0","id":1.5,"method":"ping"}""");
        _ = relay.FromClaude("""{"jsonrpc":"2.0","id":null,"method":"ping"}""");
        Assert.Equal([Refused(14, ControlClient.UnreachableError)], relay.ConnectFailed(ControlClient.UnreachableError));
    }

    [Fact]
    public void AFailedFirstHandshakeEndsTheRelay()
    {
        var refused = new ControlRelay();
        _ = refused.FromClaude(Initialize);
        Assert.Equal([
            ToClaude(McpSession.ErrorLine(McpSession.InternalErrorCode, ControlClient.NotRunningError, JsonValue.Int(0))),
            Exit(ControlOutput.UnreachableExit),
        ], refused.ConnectFailed(ControlClient.NotRunningError));
        var dropped = new ControlRelay();
        _ = dropped.FromClaude(Initialize);
        _ = dropped.ConnectSucceeded();
        // the app went before it answered initialize
        Assert.Equal([
            ToClaude(McpSession.ErrorLine(McpSession.InternalErrorCode, ControlRelay.RestartedError, JsonValue.Int(0))),
            Exit(ControlOutput.UnreachableExit),
        ], dropped.ConnectionLost());
    }

    /// <summary>An <c>initialize</c> the app refuses is neither the handshake nor kept to replay: the app going then
    /// ends the relay, as before any handshake, and the <c>initialize</c> the app accepts next is the one a new
    /// connection replays.</summary>
    [Fact]
    public void ARefusedInitializeIsNeitherTheHandshakeNorReplayed()
    {
        const string refusal = """{"error":{"code":-32602,"message":"Unsupported protocol version"},"id":0,"jsonrpc":"2.0"}""";
        var refused = new ControlRelay();
        _ = refused.FromClaude(Initialize);
        _ = refused.ConnectSucceeded();
        Assert.Equal([ToClaude(refusal)], refused.FromApp(refusal));
        Assert.Equal([Exit(ControlOutput.UnreachableExit)], refused.ConnectionLost());   // no handshake was made

        var retried = new ControlRelay();
        _ = retried.FromClaude(Initialize);
        _ = retried.ConnectSucceeded();
        _ = retried.FromApp(refusal);
        var second = Initialize.Replace("\"id\":0", "\"id\":1", StringComparison.Ordinal).Replace("2025-11-25", "2025-06-18", StringComparison.Ordinal);
        Assert.Equal([ToApp(second)], retried.FromClaude(second));
        var accepted = InitializeAnswer.Replace("\"id\":0", "\"id\":1", StringComparison.Ordinal);
        Assert.Equal([ToClaude(accepted)], retried.FromApp(accepted));
        _ = retried.FromClaude(Initialized);
        Assert.Empty(retried.ConnectionLost());   // the accepted initialize was the handshake
        Assert.Equal([Connect()], retried.FromClaude(Call(2)));
        var replay = retried.ConnectSucceeded();
        var replayed = Assert.IsType<ControlRelay.Action.ToApp>(replay[0]).Line;
        // the initialize the app accepted, not the one it refused
        Assert.Equal(ControlRelay.Message(second)!["params"], ControlRelay.Message(replayed)!["params"]);
    }

    /// <summary>Two <c>initialize</c> requests before either is answered: the one the app accepts, under its own id, is
    /// the one replayed, though another came after it.</summary>
    [Fact]
    public void TheInitializeAcceptedUnderItsOwnIdIsReplayedThoughAnotherFollowedIt()
    {
        var relay = new ControlRelay();
        _ = relay.FromClaude(Initialize);
        _ = relay.ConnectSucceeded();
        var second = Initialize.Replace("\"id\":0", "\"id\":1", StringComparison.Ordinal).Replace("2025-11-25", "2025-06-18", StringComparison.Ordinal);
        Assert.Equal([ToApp(second)], relay.FromClaude(second));
        Assert.Equal([ToClaude(InitializeAnswer)], relay.FromApp(InitializeAnswer));
        const string refusal = """{"error":{"code":-32600,"message":"Invalid Request"},"id":1,"jsonrpc":"2.0"}""";
        Assert.Equal([ToClaude(refusal)], relay.FromApp(refusal));
        _ = relay.FromClaude(Initialized);
        Assert.Empty(relay.ConnectionLost());
        Assert.Equal([Connect()], relay.FromClaude(Call(2)));
        var replayed = Assert.IsType<ControlRelay.Action.ToApp>(relay.ConnectSucceeded()[0]).Line;
        // the initialize answered with a result, not the later one
        Assert.Equal(ControlRelay.Message(Initialize)!["params"], ControlRelay.Message(replayed)!["params"]);
    }

    [Fact]
    public void WhenClaudeClosesTheRelayEndsOnceItsRequestsAreAnswered()
    {
        Assert.Equal([Exit(ControlOutput.DoneExit)], Started().ClaudeClosed());   // nothing waiting: at once
        var relay = Started();
        _ = relay.FromClaude(Call(7));
        Assert.Empty(relay.ClaudeClosed());
        Assert.Equal([ToClaude(Answer(7)), Exit(ControlOutput.DoneExit)], relay.FromApp(Answer(7)));
    }

    /// <summary>Once Claude has closed its side, whatever settles the last request ends the relay - its answer,
    /// the connection going, or a connection that could not be made - so the relay never outlives it.</summary>
    [Fact]
    public void OnceClaudeHasClosedALostOrFailedConnectionEndsTheRelay()
    {
        var lost = Started();
        _ = lost.FromClaude(Call(12));
        Assert.Empty(lost.ClaudeClosed());
        Assert.Equal([Refused(12, ControlRelay.RestartedError), Exit(ControlOutput.DoneExit)], lost.ConnectionLost());
        var failed = Started();
        _ = failed.ConnectionLost();
        Assert.Equal([Connect()], failed.FromClaude(Call(13)));
        Assert.Empty(failed.ClaudeClosed());   // the connection on its way settles the request first
        Assert.Equal([Refused(13, ControlClient.UnreachableError), Exit(ControlOutput.DoneExit)],
            failed.ConnectFailed(ControlClient.UnreachableError));
    }
}
