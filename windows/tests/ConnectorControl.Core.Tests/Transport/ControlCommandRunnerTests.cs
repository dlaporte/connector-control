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

        public void Close()
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

    [Fact]
    public void AConnectionThatEndsBeforeTheAnswerIsUnreachable()
    {
        Assert.Equal(ControlOutput.Unreachable(ControlClient.DroppedError),
            ControlCommandRunner.Run("get_status", JsonValue.Object(), false, "1.5.0", new ScriptedStream([Answered]), () => { }));
    }
}
