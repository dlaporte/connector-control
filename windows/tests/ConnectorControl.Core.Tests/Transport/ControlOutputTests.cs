using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Mirror: Tests/ConnectorControlStateTests/Transport/ControlOutputTests.swift</summary>
public class ControlOutputTests
{
    private static JsonValue Response(ControlResult result) =>
        JsonValue.Object(("jsonrpc", JsonValue.String("2.0")), ("id", JsonValue.Int(2)), ("result", result.McpResult));

    [Fact]
    public void AnAnswerPrintsItsTextOrUnderJsonItsStructure()
    {
        var done = Response(new ControlResult(JsonValue.Object(("applied", JsonValue.Bool(true))), "Turned on \u201Cx\u201D.", false));
        Assert.Equal(new ControlOutput("Turned on \u201Cx\u201D.", null, 0), ControlOutput.Answer(done, false));
        Assert.Equal(new ControlOutput("""{"applied":true}""", null, 0), ControlOutput.Answer(done, true));
    }

    [Fact]
    public void ARefusalGoesToStderrAndExitsOne()
    {
        var refused = Response(ControlResult.Refusal("No such thing."));
        Assert.Equal(new ControlOutput(null, "No such thing.", 1), ControlOutput.Answer(refused, false));
        Assert.Equal(new ControlOutput("""{"error":"No such thing."}""", null, 1), ControlOutput.Answer(refused, true));
    }

    [Fact]
    public void AnErrorTheSessionAnsweredIsARefusal()
    {
        var line = McpSession.ErrorLine(McpSession.InvalidParamsCode, ControlTools.NeedsNewerAppError("9.0.0"), JsonValue.Int(2));
        var error = JsonValue.Parse(line);
        Assert.Equal(new ControlOutput(null, ControlTools.NeedsNewerAppError("9.0.0"), 1), ControlOutput.Answer(error, false));
        Assert.Equal(JsonValue.Object(("error", JsonValue.String(ControlTools.NeedsNewerAppError("9.0.0")))).CompactLine(),
            ControlOutput.Answer(error, true).Stdout);
    }

    /// <summary><c>--json</c> is for reading and scripts: a path comes out as written. The wire format still escapes it.</summary>
    [Fact]
    public void JsonOutputWritesSlashesAsTheyAre()
    {
        const string path = "/Users/me/work/team.json";
        var done = Response(new ControlResult(JsonValue.Object(("path", JsonValue.String(path))), "x", false));
        Assert.Equal("""{"path":"/Users/me/work/team.json"}""", ControlOutput.Answer(done, true).Stdout);
        Assert.Equal("""{"error":"/Users/me/work/team.json"}""", ControlOutput.Answer(Response(ControlResult.Refusal(path)), true).Stdout);
        Assert.Equal("\"\\/Users\\/me\\/work\\/team.json\"", JsonValue.String(path).CompactLine());
        Assert.Equal("\"/Users/me/work/team.json\"", JsonValue.String(path).CompactLine(escapeSlashes: false));
    }

    [Fact]
    public void TheExitCodes()
    {
        Assert.Equal([0, 1, 2, 3], [ControlOutput.DoneExit, ControlOutput.RefusedExit, ControlOutput.UsageExit, ControlOutput.UnreachableExit]);
        Assert.Equal(new ControlOutput(null, "x", 2), ControlOutput.Usage("x"));
        Assert.Equal(new ControlOutput(null, "y", 3), ControlOutput.Unreachable("y"));
    }
}
