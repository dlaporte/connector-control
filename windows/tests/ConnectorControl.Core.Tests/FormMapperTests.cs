namespace ConnectorControl.Core.Tests;

public class FormMapperTests
{
    private static Dictionary<string, string> Env(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);

    [Fact]
    public void CleanLocalConfigIsLossless()
    {
        var analysis = FormMapper.Analyze(JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("pkg")])),
            ("env", JsonValue.Object(("KEY", JsonValue.String("v"))))));
        Assert.True(analysis.IsLossless);
        Assert.Equal(new FormModel("npx", ["-y", "pkg"], Env(("KEY", "v"))), analysis.Model);
    }

    [Fact]
    public void UnknownKeysArePreservedNotLost()
    {
        var analysis = FormMapper.Analyze(JsonValue.Object(
            ("command", JsonValue.String("x")),
            ("type", JsonValue.String("http")),
            ("headers", JsonValue.Object(("Authorization", JsonValue.String("Bearer t"))))));
        Assert.True(analysis.IsLossless);
        Assert.Equal(new HashSet<string> { "type", "headers" }, analysis.Model.Additional.Keys.ToHashSet());
    }

    [Fact]
    public void StructuralViolationsAreListedAsLost()
    {
        var analysis = FormMapper.Analyze(JsonValue.Object(
            ("command", JsonValue.Int(5)),
            ("args", JsonValue.Array([JsonValue.String("ok"), JsonValue.Object(), JsonValue.Int(3)])),
            ("env", JsonValue.Object(("GOOD", JsonValue.String("y")), ("BAD", JsonValue.Int(1))))));
        Assert.False(analysis.IsLossless);
        Assert.Equal(["args[1] (object)", "args[2] (number)", "command (number)", "env.BAD (number)"], analysis.Lost);
        Assert.Equal(["ok"], analysis.Model.Args);
        Assert.Equal(Env(("GOOD", "y")), analysis.Model.Env);
        Assert.Equal("", analysis.Model.Command);
    }

    [Fact]
    public void NonObjectArgsOrEnvAreLost()
    {
        var analysis = FormMapper.Analyze(JsonValue.Object(
            ("command", JsonValue.String("x")),
            ("args", JsonValue.String("not an array")),
            ("env", JsonValue.Array([]))));
        Assert.Equal(["args (not an array)", "env (not an object)"], analysis.Lost);
    }

    [Fact]
    public void NonObjectConfigIsEntirelyLost()
    {
        Assert.Equal(["entire configuration (not a JSON object)"], FormMapper.Analyze(JsonValue.String("weird")).Lost);
    }

    [Fact]
    public void SerializeRoundTripsLosslessConfig()
    {
        var original = JsonValue.Object(
            ("command", JsonValue.String("npx")),
            ("args", JsonValue.Array([JsonValue.String("-y"), JsonValue.String("pkg")])),
            ("env", JsonValue.Object(("K", JsonValue.String("v")))),
            ("headers", JsonValue.Object(("H", JsonValue.String("x")))));
        Assert.Equal(original, FormMapper.Serialize(FormMapper.Analyze(original).Model));
    }

    [Fact]
    public void SerializeOmitsEmptyArgsAndEnv()
    {
        Assert.Equal(JsonValue.Object(("command", JsonValue.String("swift"))), FormMapper.Serialize(new FormModel("swift")));
    }

    [Fact]
    public void CommandlessConfigRoundTripsWithoutInjectingCommand()
    {
        var original = JsonValue.Object(("type", JsonValue.String("http")), ("url", JsonValue.String("https://example.com/mcp")));
        var analysis = FormMapper.Analyze(original);
        Assert.True(analysis.IsLossless);
        Assert.Equal(original, FormMapper.Serialize(analysis.Model));
    }
}
