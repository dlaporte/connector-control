using System.Text;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Mirror: Tests/ConnectorControlStateTests/Transport/ControlCommandRunnerTests.swift</summary>
public class ControlCommandRunnerTests
{
    /// <summary>Hands the runner the lines the test wrote for it, one per read, and keeps what it sends.</summary>
    private sealed class ScriptedStream(IEnumerable<string> replies) : IControlStream
    {
        private readonly Queue<string> replies = new(replies);
        public List<byte> Written { get; } = [];

        public byte[] Read(int maximum) => replies.Count == 0 ? [] : Encoding.UTF8.GetBytes(replies.Dequeue() + "\n");

        public bool Write(byte[] data)
        {
            Written.AddRange(data);
            return true;
        }

        public bool Closed { get; private set; }

        public void Close() => Closed = true;

        public void CloseWrite()
        {
        }
    }

    private const string Answered = """{"id":1,"jsonrpc":"2.0","result":{}}""";

    [Fact]
    public void TheHelloHandshakeAndCallGoOutTogether()
    {
        Assert.Equal(
        [
            """{"client":"cli","version":"1.5.0"}""",
            """{"id":1,"jsonrpc":"2.0","method":"initialize","params":{"capabilities":{},"clientInfo":{"name":"connector-control","version":"1.5.0"},"protocolVersion":"2025-11-25"}}""",
            """{"jsonrpc":"2.0","method":"notifications\/initialized"}""",
            """{"id":2,"jsonrpc":"2.0","method":"tools\/call","params":{"_meta":{"progressToken":"cli"},"arguments":{},"name":"get_status"}}""",
        ], ControlCommandRunner.OpeningLines("get_status", JsonValue.Object(), "1.5.0"));
    }

    [Fact]
    public void TheCallsAnswerIsWhatPrintsAndProgressIsNoticedOnce()
    {
        var stream = new ScriptedStream(
        [
            Answered,
            """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":5,"progressToken":"cli"}}""",
            """{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":10,"progressToken":"cli"}}""",
            """{"id":2,"jsonrpc":"2.0","result":{"content":[{"text":"Did it.","type":"text"}],"isError":false,"structuredContent":{}}}""",
        ]);
        var notices = 0;
        var output = ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", stream, () => notices++);
        Assert.Equal(new ControlOutput("Did it.", null, 0), output);
        Assert.Equal(1, notices);
        Assert.Equal(string.Concat(ControlCommandRunner.OpeningLines("get_status", JsonValue.Object(), "1.5.0").Select(line => line + "\n")),
            Encoding.UTF8.GetString([.. stream.Written]));
    }

    [Fact]
    public void AnErrorWithNoIdIsTheAnswer()
    {
        var stream = new ScriptedStream(["""{"error":{"code":-32600,"message":"Message too large"},"id":null,"jsonrpc":"2.0"}"""]);
        Assert.Equal(new ControlOutput(null, McpSession.TooLargeMessage, 1),
            ControlCommandRunner.Run("add_connector", JsonValue.Object(), false, "1.5.0", stream, () => { }));
    }

    [Fact]
    public void AnAnswerPastTheLimitEndsTheCommandAsUnreachable()
    {
        var stream = new ScriptedStream([new string('x', 100)]);
        Assert.Equal(ControlOutput.Unreachable(ControlClient.DroppedError),
            ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", stream, () => { }, limit: 64));
    }

    /// <summary>An answer that follows the oversized line is never reached, so only a working limit ends in the
    /// dropped result: a broken one would print the answer.</summary>
    [Fact]
    public void AnOversizedLineIsNeverFollowedByTheAnswerThatComesAfterIt()
    {
        var stream = new ScriptedStream([new string('x', 100), AnswerLine(0)]);
        Assert.Equal(ControlOutput.Unreachable(ControlClient.DroppedError),
            ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", stream, () => { }, limit: 64));
    }

    [Fact]
    public void ALineJustOverTheLimitIsRefusedAndOneJustUnderIsAccepted()
    {
        var limit = Encoding.UTF8.GetByteCount(AnswerLine(0)) + 10;
        Assert.Equal(ControlOutput.Unreachable(ControlClient.DroppedError),
            ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", new ScriptedStream([AnswerLine(11)]), () => { }, limit: limit));
        Assert.Equal(new ControlOutput(new string('p', 10), null, 0),
            ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", new ScriptedStream([AnswerLine(10)]), () => { }, limit: limit));
    }

    /// <summary>The default is the client's 64 MB, not the framer's 1 MB, which a long answer would pass.</summary>
    [Fact]
    public void TheDefaultLimitIsTheClientsAnswerLimit()
    {
        Assert.Equal(64 * 1_048_576, ControlClient.MaxAnswerBytes);
        var output = ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0",
            new ScriptedStream([AnswerLine(McpSession.MaxMessageBytes + 1)]), () => { });
        Assert.Equal(0, output.ExitCode);
    }

    /// <summary>The call's answer with <paramref name="padded"/> bytes of text in it.</summary>
    private static string AnswerLine(int padded) =>
        "{\"id\":2,\"jsonrpc\":\"2.0\",\"result\":{\"content\":[{\"text\":\"" + new string('p', padded)
        + "\",\"type\":\"text\"}],\"isError\":false,\"structuredContent\":{}}}";

    [Fact]
    public void AConnectionThatEndsBeforeTheAnswerIsUnreachable()
    {
        Assert.Equal(ControlOutput.Unreachable(ControlClient.DroppedError),
            ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", new ScriptedStream([Answered]), () => { }));
    }

    /// <summary>The command runs over the connection the client makes, and closes it once answered.</summary>
    [Fact]
    public void ACommandRunsOverTheClientsConnection()
    {
        var stream = new ScriptedStream([Answered, AnswerLine(1)]);
        var client = new ControlClient(() => stream, null, null);
        Assert.Equal(new ControlOutput("p", null, ControlOutput.DoneExit),
            ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", client, () => { }));
        Assert.True(stream.Closed);
    }

    /// <summary>What the command line prints when the tool runs elevated and nothing answers: why it started
    /// nothing, on stderr as text — under --json too — with exit 3, and no "Starting…" before it.</summary>
    [Fact]
    public void AnElevatedToolPrintsWhyItStartedNothingAndExitsThree()
    {
        var launches = 0;
        var announced = 0;
        var clock = TimeSpan.Zero;
        foreach (var json in new[] { false, true })
        {
            var client = new ControlClient(() => null, () => { launches++; return true; }, () => announced++,
                wait => clock += wait, () => clock, elevated: true);
            Assert.Equal(new ControlOutput(null, ControlClient.ElevatedError, ControlOutput.UnreachableExit),
                ControlCommandRunner.Run("get_status", JsonValue.Object(), json, "1.5.0", client, () => Assert.Fail("nothing ran")));
        }
        Assert.Equal([0, 0], [launches, announced]);
        Assert.Equal(TimeSpan.Zero, clock);
        Assert.Equal(3, ControlOutput.UnreachableExit);
    }
}
