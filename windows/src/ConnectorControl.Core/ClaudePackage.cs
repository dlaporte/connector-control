using System.Text.RegularExpressions;

namespace ConnectorControl.Core;

/// <summary>
/// The one rule for which MSIX package is Claude Desktop's. Detection (the WinRT package query and the
/// %LOCALAPPDATA%\Packages scan), the config-path search, and the Restart Claude launch check all judge
/// a package family by it, so a package another publisher named "Claude" is invisible to every one of them.
/// </summary>
public static class ClaudePackage
{
    /// <summary>Claude Desktop's Microsoft Store package family; the publisher hash pins Anthropic.</summary>
    public const string StoreFamily = "Claude_pzs8sxrjxfjjc";

    /// <summary>
    /// <c>Name_publisherhash!AppId</c> and nothing else: the string is concatenated into explorer's
    /// arguments, so a quote, a space or a second token must never get that far.
    /// </summary>
    private static readonly Regex AumidGrammar = new(@"^(?<family>[A-Za-z0-9][A-Za-z0-9.\-]*_[a-z0-9]{13})![A-Za-z0-9][A-Za-z0-9.\-]*\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Claude's own package family: the Store family exactly (its hash is Anthropic's publisher), or an
    /// <c>Anthropic.Claude…</c> family until its publisher is recorded too. Any other publisher's package
    /// called "Claude" is not it.
    /// </summary>
    public static bool IsClaudeFamily(string family) =>
        family == StoreFamily || family.StartsWith("Anthropic.Claude", StringComparison.Ordinal);

    /// <summary>
    /// settings.json is the user's to edit, so an AUMID there is a string like any other. Only a
    /// well-formed AUMID naming Claude's own package family may be started in Claude's place.
    /// </summary>
    public static bool IsClaudeAumid(string aumid)
    {
        var match = AumidGrammar.Match(aumid);
        return match.Success && IsClaudeFamily(match.Groups["family"].Value);
    }
}
