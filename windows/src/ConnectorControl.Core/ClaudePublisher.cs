namespace ConnectorControl.Core;

/// <summary>
/// Who may sign the claude.exe that Restart Claude launches. The Mac pins Anthropic's Apple Team ID;
/// Authenticode has no such field, so the closest thing is the certificate's organization (O=), the
/// attribute a CA vets before issuing an OV/EV code-signing certificate. A CN or OU that merely contains
/// the word is anyone's to register — the check this replaced was a substring over the whole subject.
/// </summary>
public static class ClaudePublisher
{
    /// <summary>
    /// Accepted organizations after <see cref="SignerIdentity.NormalizeOrganization"/>. Anthropic's public
    /// spellings are "Anthropic" and "Anthropic, PBC"; the exact value on a real claude.exe is what
    /// windows/tools/probe-claude.ps1 (section "Signer") prints — a rotation to another spelling is a
    /// one-line addition here.
    /// </summary>
    public static readonly string[] ExpectedOrganizations = ["anthropic", "anthropic pbc"];

    /// <summary>The fix every refusal in this app ends with, worded the same way everywhere it appears.</summary>
    public const string ChooseClaude = "Choose the real Claude Desktop under Settings ▸ Claude.";

    /// <summary>
    /// Null when <paramref name="subject"/> — the signer's distinguished name as X509Certificate.Subject
    /// renders it — names Anthropic as the organization; otherwise the user-facing reason
    /// <paramref name="fileName"/> must not be launched.
    /// </summary>
    public static string? SubjectProblem(string subject, string fileName) => SubjectProblem(SignerIdentity.Parse(subject), fileName);

    /// <summary>The <see cref="SignerIdentity"/> form, for a caller that has already parsed the signer.</summary>
    public static string? SubjectProblem(SignerIdentity signer, string fileName)
    {
        if (signer.Organization is not { } organization)
        {
            return $"{fileName} is signed by \"{signer.Subject}\", which names no organization. {ChooseClaude}";
        }
        if (!ExpectedOrganizations.Contains(organization, StringComparer.Ordinal))
        {
            return $"{fileName} is signed by \"{signer.Subject}\", not by Anthropic. {ChooseClaude}";
        }
        return null;
    }
}
