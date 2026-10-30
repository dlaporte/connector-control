using System.Text;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/McpSessionTests.swift</summary>
public class McpSessionTests
{
    private sealed class FakeHost : IMcpToolHost
    {
        public sealed record Call(
            string Name, JsonValue Arguments, JsonValue? Token, Action<double> Progress, Action<JsonValue> Completion);

        public List<Call> Calls { get; } = [];

        public IReadOnlyList<JsonValue> ListTools() => [JsonValue.Object(("name", JsonValue.String("echo")))];

        public string UnknownToolMessage(string name) => $"No {name} here";

        public void CallTool(string name, JsonValue arguments, JsonValue? progressToken,
                             Action<double> progress, Action<JsonValue> completion) =>
            Calls.Add(new Call(name, arguments, progressToken, progress, completion));
    }

    private readonly FakeHost host = new();
    private readonly List<string> sent = [];
    private readonly McpSession session;

    public McpSessionTests()
    {
        session = new McpSession(host, "9.9.9", sent.Add);
    }

    private void Initialize(string version = "2025-06-18") =>
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"%V%","capabilities":{},"clientInfo":{"name":"claude-ai","version":"0.1"}}}""".Replace("%V%", version));

    [Fact]
    public void InitializeEchoesASupportedVersionAndNamesTheServer()
    {
        Initialize();
        Assert.Equal(
            ["""{"id":1,"jsonrpc":"2.0","result":{"capabilities":{"tools":{}},"protocolVersion":"2025-06-18","serverInfo":{"name":"connector-control","version":"9.9.9"}}}"""],
            sent);
        Assert.Equal("claude-ai", session.ClientName);
        Assert.True(session.IsInitialized);
    }

    [Fact]
    public void AnUnknownVersionIsAnsweredWithTheNewest()
    {
        Initialize("1999-01-01");
        Assert.Contains("\"protocolVersion\":\"2025-11-25\"", sent[0]);
    }

    [Fact]
    public void PingIsAnsweredEvenBeforeInitialize()
    {
        session.Receive("""{"jsonrpc":"2.0","id":"p","method":"ping"}""");
        Assert.Equal(["""{"id":"p","jsonrpc":"2.0","result":{}}"""], sent);
    }

    [Fact]
    public void ARequestBeforeInitializeIsRefused()
    {
        session.Receive("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal(["""{"error":{"code":-32002,"message":"Server not initialized"},"id":2,"jsonrpc":"2.0"}"""], sent);
    }

    [Fact]
    public void ToolsListReturnsTheHostsTools()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal("""{"id":2,"jsonrpc":"2.0","result":{"tools":[{"name":"echo"}]}}""", sent[^1]);
    }

    [Fact]
    public void ToolsCallRunsTheToolAndAnswersWithItsResult()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"a":1}}}""");
        var call = Assert.Single(host.Calls);
        Assert.Equal("echo", call.Name);
        Assert.Equal(JsonValue.Object(("a", JsonValue.Int(1))), call.Arguments);
        Assert.Null(call.Token);
        Assert.Single(sent);
        call.Completion(JsonValue.Object(("isError", JsonValue.Bool(false))));
        Assert.Equal("""{"id":3,"jsonrpc":"2.0","result":{"isError":false}}""", sent[^1]);
        call.Completion(JsonValue.Object(("isError", JsonValue.Bool(true))));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void ProgressIsSentOnlyWhenTheClientGaveAToken()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","_meta":{"progressToken":"t1"}}}""");
        session.Receive("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo"}}""");
        host.Calls[0].Progress(5);
        host.Calls[1].Progress(5);
        Assert.Equal(
            ["""{"jsonrpc":"2.0","method":"notifications\/progress","params":{"progress":5,"progressToken":"t1"}}"""],
            sent.Skip(1).ToList());
        Assert.Equal(JsonValue.String("t1"), host.Calls[0].Token);
    }

    [Fact]
    public void ACancelledRequestIsNeverAnswered()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","_meta":{"progressToken":7}}}""");
        session.Receive("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":6,"reason":"timeout"}}""");
        host.Calls[0].Progress(5);
        host.Calls[0].Completion(JsonValue.Object());
        Assert.Single(sent);
    }

    [Fact]
    public void AnEscapedLoneSurrogateIsAParseError()
    {
        const string ParseError = """{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""";
        session.Receive("""{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{"a":"\ud800"}}""");
        session.Receive("""{"jsonrpc":"2.0","id":5,"method":"tools/list","params":{"\udc00":1}}""");
        Assert.Equal([ParseError, ParseError], sent);
    }

    /// <summary>A trailing comma is not JSON: a parse error, as on the Mac.</summary>
    [Fact]
    public void ATrailingCommaIsAParseError()
    {
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"ping",}""");
        session.Receive("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"a":[1,]}}""");
        Assert.Equal([
            """{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""",
            """{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""",
        ], sent);
    }

    [Fact]
    public void MalformedJsonIsAParseError()
    {
        session.Receive("{nope");
        Assert.Equal(["""{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"""], sent);
    }

    [Fact]
    public void AnythingButAJsonRpcRequestIsAnInvalidRequest()
    {
        session.Receive("""{"id":3,"method":"ping"}""");
        session.Receive("[1,2]");
        session.Receive("""{"jsonrpc":"2.0","id":true,"method":"ping"}""");
        Assert.Equal(
            [
                """{"error":{"code":-32600,"message":"Invalid Request"},"id":3,"jsonrpc":"2.0"}""",
                """{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}""",
                """{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}""",
            ],
            sent);
    }

    [Fact]
    public void AnUnknownMethodIsMethodNotFound()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}""");
        Assert.Equal("""{"error":{"code":-32601,"message":"Method not found"},"id":4,"jsonrpc":"2.0"}""", sent[^1]);
    }

    [Fact]
    public void AnUnknownToolAndBadArgumentsAreInvalidParams()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nope"}}""");
        session.Receive("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","arguments":[1]}}""");
        session.Receive("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""");
        Assert.Equal(
            [
                """{"error":{"code":-32602,"message":"No nope here"},"id":5,"jsonrpc":"2.0"}""",
                """{"error":{"code":-32602,"message":"Invalid params"},"id":6,"jsonrpc":"2.0"}""",
                """{"error":{"code":-32602,"message":"Invalid params"},"id":7,"jsonrpc":"2.0"}""",
            ],
            sent.Skip(1).ToList());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AMessageOverOneMegabyteIsRefusedUnread()
    {
        session.Receive(new string('x', McpSession.MaxMessageBytes + 1));
        Assert.Equal(["""{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"""], sent);
    }

    [Fact]
    public void NotificationsAndClientResponsesAreNeverAnswered()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        session.Receive("""{"jsonrpc":"2.0","id":99,"result":{}}""");
        session.Receive("");
        Assert.Single(sent);
    }

    [Fact]
    public void ProgressThatIsNotFiniteIsDroppedAndFractionsSurvive()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","_meta":{"progressToken":"t"}}}""");
        host.Calls[0].Progress(double.NaN);
        host.Calls[0].Progress(double.PositiveInfinity);
        host.Calls[0].Progress(double.NegativeInfinity);
        Assert.Single(sent);
        host.Calls[0].Progress(0.5);
        Assert.Equal("""{"jsonrpc":"2.0","method":"notifications\/progress","params":{"progress":0.5,"progressToken":"t"}}""", sent[^1]);
    }

    private static string PingLine(string padding) =>
        """{"jsonrpc":"2.0","id":1,"method":"ping","pad":"%P%"}""".Replace("%P%", padding);

    [Fact]
    public void NestingDeeperThanSixtyFourIsAParseError()
    {
        var ok = """{"jsonrpc":"2.0","id":1,"method":"ping","p":""" + new string('[', 63) + new string(']', 63) + "}";
        var deep = """{"jsonrpc":"2.0","id":1,"method":"ping","p":""" + new string('[', 64) + new string(']', 64) + "}";
        session.Receive(ok);
        session.Receive(deep);
        Assert.Equal(
            [
                """{"id":1,"jsonrpc":"2.0","result":{}}""",
                """{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}""",
            ],
            sent);
    }

    [Fact]
    public void BracketsInsideStringsDoNotCountAsNesting()
    {
        session.Receive(PingLine(new string('[', 200) + "\\\"" + new string('[', 200)));
        Assert.Equal(["""{"id":1,"jsonrpc":"2.0","result":{}}"""], sent);
    }

    [Fact]
    public void TwoObjectsOnOneLineAreAParseError()
    {
        session.Receive("""{"jsonrpc":"2.0","id":1,"method":"ping"}{"b":2}""");
        Assert.Equal(["""{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"""], sent);
    }

    [Fact]
    public void AnIntegerIdBeyondSixtyFourBitsIsAnInvalidRequest()
    {
        session.Receive("""{"jsonrpc":"2.0","id":123456789012345678901234567890,"method":"ping"}""");
        Assert.Equal(["""{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"""], sent);
    }

    [Fact]
    public void AWhitespaceOnlyLineIsIgnoredAndANullIdIsInvalid()
    {
        session.Receive("  \t ");
        session.Receive("""{"jsonrpc":"2.0","id":null,"method":"ping"}""");
        Assert.Equal(["""{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}"""], sent);
    }

    [Fact]
    public void ReplacementCharactersAlreadyInTextAreReadAsText()
    {
        // Text that already holds U+FFFD is text; bytes that are not UTF-8 never become text (below).
        session.Receive(PingLine("\uFFFD\uFFFD"));
        Assert.Equal(["""{"id":1,"jsonrpc":"2.0","result":{}}"""], sent);
    }

    [Fact]
    public void AnAnswerJsonCannotHoldCannotBeBuilt()
    {
        // Stands in for the Mac's testAnAnswerJSONCannotHoldIsAnInternalErrorNotSilence: a C# JsonValue
        // refuses NaN and infinity, so no answer is ever unwritable.
        Assert.Throws<ArgumentException>(() => JsonValue.Double(double.NaN));
        Assert.Throws<ArgumentException>(() => JsonValue.Double(double.PositiveInfinity));
    }

    /// <summary>An answer nested deeper than any reader of ours goes is refused, or the client waits on that id for
    /// good. The whole line counts: the envelope and the result are two levels, so 62 arrays inside make 64.</summary>
    [Fact]
    public void AnAnswerNestedPastTheLimitIsAnInternalErrorNotSilence()
    {
        static JsonValue Nested(int depth)
        {
            var value = JsonValue.Int(1);
            for (var i = 0; i < depth; i++)
            {
                value = JsonValue.Array([value]);
            }
            return value;
        }
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"echo"}}""");
        host.Calls[^1].Completion(JsonValue.Object(("value", Nested(McpSession.MaxNestingDepth - 2))));
        var answer = sent[^1];
        Assert.StartsWith("""{"id":9,"jsonrpc":"2.0","result":{"value":[[[[""", answer, StringComparison.Ordinal);
        Assert.True(McpSession.Nests(McpSession.MaxNestingDepth, answer), "a line exactly at the limit goes out");
        session.Receive("""{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"echo"}}""");
        host.Calls[^1].Completion(JsonValue.Object(("value", Nested(McpSession.MaxNestingDepth - 1))));
        Assert.Equal("""{"error":{"code":-32603,"message":"Internal error"},"id":10,"jsonrpc":"2.0"}""", sent[^1]);
    }

    [Fact]
    public void BytesThatAreNotUtf8AreAParseErrorBeforeAnythingDecodesThem()
    {
        byte[] prefix = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"x\":\""u8.ToArray();
        byte[] suffix = "\"}}"u8.ToArray();
        byte[][] invalid = [[0xC3, 0x28], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0xFF], [0xF4, 0x90, 0x80, 0x80], [0xE2]];
        foreach (var bad in invalid)
        {
            sent.Clear();
            session.Receive([.. prefix, .. bad, .. suffix]);
            Assert.Equal(["""{"error":{"code":-32700,"message":"Parse error"},"id":null,"jsonrpc":"2.0"}"""], sent);
        }
        sent.Clear();
        session.Receive(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"é","method":"ping"}"""));
        Assert.Equal(["""{"id":"é","jsonrpc":"2.0","result":{}}"""], sent); // valid UTF-8 is read as it always was
    }

    [Fact]
    public void BytesPastTheLimitAndAFramersReportAreRefusedUnread()
    {
        // 0xFF is not UTF-8: only a size check that runs before decoding answers "too large" for it.
        session.Receive(Enumerable.Repeat((byte)0xFF, McpSession.MaxMessageBytes + 1).ToArray());
        session.ReceiveTooLarge();
        Assert.Equal(
            Enumerable.Repeat("""{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}""", 2),
            sent);
    }

    [Fact]
    public void ErrorLinesAreWrittenAsTheSessionWritesThem()
    {
        Assert.Equal(
            """{"error":{"code":-32603,"message":"Internal error"},"id":4,"jsonrpc":"2.0"}""",
            McpSession.ErrorLine(McpSession.InternalErrorCode, McpSession.InternalErrorMessage, JsonValue.Int(4)));
    }

    [Fact]
    public void TheSizeLimitCountsBytesNotCharacters()
    {
        var baseBytes = Encoding.UTF8.GetByteCount(PingLine(""));
        var room = McpSession.MaxMessageBytes - baseBytes;
        var padding = "";
        if (room % 2 == 1) { padding = "x"; room -= 1; }
        padding += new string('é', room / 2);
        var exact = PingLine(padding);
        Assert.Equal(McpSession.MaxMessageBytes, Encoding.UTF8.GetByteCount(exact));
        session.Receive(exact);
        session.Receive(PingLine(padding + "x"));
        Assert.Equal(
            [
                """{"id":1,"jsonrpc":"2.0","result":{}}""",
                """{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}""",
            ],
            sent);
    }

    [Fact]
    public void ARequestIdAlreadyRunningIsRefusedWithoutAnId()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo"}}""");
        session.Receive("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo"}}""");
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":null,"jsonrpc":"2.0"}""", sent[^1]);
        Assert.Single(host.Calls);
        host.Calls[0].Completion(JsonValue.Object());
        Assert.Equal("""{"id":3,"jsonrpc":"2.0","result":{}}""", sent[^1]);
    }

    /// <summary>Ids are compared ordinally, as the Mac compares them: an id composed another way is another
    /// request, run and answered, and cancelling one leaves the other answered.</summary>
    [Fact]
    public void AnIdComposedAnotherWayIsAnotherRequest()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":"caf\u00e9","method":"tools/call","params":{"name":"echo"}}""");
        session.Receive("""{"jsonrpc":"2.0","id":"cafe\u0301","method":"tools/call","params":{"name":"echo"}}""");
        Assert.Equal(2, host.Calls.Count);
        session.Receive("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"cafe\u0301"}}""");
        host.Calls[0].Completion(JsonValue.Object());
        host.Calls[1].Completion(JsonValue.Object());
        Assert.Equal(2, sent.Count);
        Assert.Equal("{\"id\":\"caf\u00E9\",\"jsonrpc\":\"2.0\",\"result\":{}}", sent[^1]);
    }

    [Fact]
    public void ASecondInitializeIsRefusedAndKeepsTheFirstName()
    {
        Initialize();
        session.Receive("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"someone-else"}}}""");
        Assert.Equal("""{"error":{"code":-32600,"message":"Invalid Request"},"id":2,"jsonrpc":"2.0"}""", sent[^1]);
        Assert.Equal("claude-ai", session.ClientName);
    }

    [Fact]
    public void CompactLinesAreOneLineKeySortedJson()
    {
        Assert.Equal(
            """{"a":"x\/y “q”","b":1,"c":[true,null]}""",
            JsonValue.Object(
                ("b", JsonValue.Int(1)),
                ("a", JsonValue.String("x/y “q”")),
                ("c", JsonValue.Array([JsonValue.Bool(true), JsonValue.Null]))).CompactLine());
    }
}
