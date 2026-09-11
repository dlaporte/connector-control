namespace ConnectorControl.Core.Tests;

public class ClaudePackageTests
{
    [Theory]
    [InlineData("Claude_pzs8sxrjxfjjc!Claude", true)]
    [InlineData("Anthropic.ClaudeDesktop_8wekyb3d8bbwe!App", true)]
    [InlineData("Claude_abcdefghjkmnp!Claude", false)]                 // a sideloaded "Claude" from another publisher
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", false)]
    [InlineData("Claude_pzs8sxrjxfjjc!Claude\" --evil", false)]        // anything after the app id
    [InlineData("Claude_pzs8sxrjxfjjc!Claude App", false)]             // whitespace
    [InlineData("Claude_pzs8sxrjxfjjc", false)]                        // no app id
    [InlineData("Claude_pzs8sxrjxfjjc!Claude\n", false)]              // a trailing newline: $ would accept it, \z does not
    [InlineData("claude_pzs8sxrjxfjjc!Claude", false)]                 // family names are case-sensitive
    public void IsClaudeAumidRequiresTheGrammarAndTheKnownFamily(string target, bool expected)
    {
        Assert.Equal(expected, ClaudePackage.IsClaudeAumid(target));
    }
}
