using System.Text;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Mirror: Tests/ConnectorControlStateTests/Transport/ControlHelloTests.swift</summary>
public class ControlHelloTests
{
    private static ControlHello Parse(string text) => ControlHello.Parse(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void OnlyAWellFormedCommandLineHelloIsTheCommandLine()
    {
        Assert.Equal(new ControlHello(true, "1.5.0"), Parse("""{"client":"cli","version":"1.5.0"}"""));
        // other keys are ignored, so a later tool can say more
        Assert.Equal(new ControlHello(true, "1.5.0"), Parse("""{"version":"1.5.0","client":"cli","pid":7}"""));
        Assert.Equal(new ControlHello(false, "1.5.0"), Parse("""{"client":"relay","version":"1.5.0"}"""));
    }

    [Fact]
    public void AnythingElseIsTheRelay()
    {
        string[] notCli =
        [
            """{"client":"cli"}""", """{"client":"cli","version":""}""", """{"client":"cli","version":1}""",
            """{"client":"CLI","version":"1.5.0"}""", """{"client":" cli","version":"1.5.0"}""",
            """["cli","1.5.0"]""", "\"cli\"", "", "cli", "{\"client\":\"cli\",\"version\":\"1.5.0\"",
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{}}""",
        ];
        foreach (var text in notCli)
        {
            Assert.False(Parse(text).IsCli, text);
        }
        Assert.False(ControlHello.Parse([0x7B, 0xFF, 0x7D]).IsCli, "not UTF-8");
        Assert.False(Parse(new string('[', 100_000)).IsCli, "nested past the limit, never parsed");
        // a relay's version still counts when a tool is missing
        Assert.Equal("1.5.0", Parse("""{"client":"CLI","version":"1.5.0"}""").Version);
        Assert.Equal(new ControlHello(false, null), ControlHello.Unknown);
    }

    [Fact]
    public void AHelloThatIsNotStrictUtf8IsTheRelayEvenWhenItsJsonIsOtherwiseACommandLineHello()
    {
        // A lossy decode would turn the stray byte into U+FFFD and still read a "cli" hello.
        byte[] bytes =
        [
            .. Encoding.UTF8.GetBytes("{\"client\":\"cli\",\"version\":\"1.5.0\",\"x\":\""),
            0xFF,
            .. Encoding.UTF8.GetBytes("\"}"),
        ];
        Assert.Equal(ControlHello.Unknown, ControlHello.Parse(bytes));
    }

    [Fact]
    public void AnEscapedLoneSurrogateIsTheRelay()
    {
        Assert.Equal(ControlHello.Unknown, Parse("""{"client":"cli","version":"\ud800"}"""));
        Assert.Equal(ControlHello.Unknown, Parse("""{"client":"cli","version":"1.5.0","\udc00":1}"""));
    }

    [Fact]
    public void TheHelloLineIsCompactKeySortedJson()
    {
        Assert.Equal("""{"client":"cli","version":"1.5.0"}""", ControlHello.Line(ControlHello.CliClient, "1.5.0"));
        Assert.Equal("""{"client":"relay","version":"1.5.0"}""", ControlHello.Line(ControlHello.RelayClient, "1.5.0"));
    }
}
