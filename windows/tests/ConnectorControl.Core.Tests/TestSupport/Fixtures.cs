namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>Reads files from Tests/Fixtures (copied next to the test binary by the csproj).</summary>
public static class Fixtures
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    public static string RealisticClaudeConfig => Text("realistic_claude_config.json");
}
