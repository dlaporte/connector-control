namespace ConnectorControl.Core;

/// <summary>
/// Warnings for the publish preview, never edits: the exporter knows env values and the remote
/// auth fields are secrets, but a token typed into an argument is indistinguishable from data.
/// These patterns catch the common shapes; a placeholder marker is by definition not a secret.
/// </summary>
public static class CredentialHeuristics
{
    private static readonly string[] Prefixes = ["sk-", "ghp_", "xox", "Bearer "];

    public static bool LooksLikeCredential(string value)
    {
        if (Placeholder.ContainsMarker(value))
        {
            return false;
        }
        if (Prefixes.Any(p => value.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }
        if (value.Length < 32 || value.Contains(' ') || value.Contains('/'))
        {
            return false;
        }
        // By general category: a letter is L*, a digit is a decimal digit (Nd), as the Mac reads them.
        return value.Any(char.IsLetter) && value.Any(char.IsDigit);
    }
}
