using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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
    /// Accepted organizations after <see cref="NormalizeOrganization"/>. Anthropic's public spellings are
    /// "Anthropic" and "Anthropic, PBC"; the exact value on a real claude.exe is what
    /// windows/scripts/probe-claude.ps1 (section "Signer") prints — a rotation to another spelling is a
    /// one-line addition here.
    /// </summary>
    public static readonly string[] ExpectedOrganizations = ["anthropic", "anthropic pbc"];

    private const string ChooseClaude = "Choose Claude Desktop's own claude.exe under Settings ▸ Claude.";

    /// <summary>
    /// Null when <paramref name="subject"/> — the signer's distinguished name as X509Certificate.Subject
    /// renders it — names Anthropic as the organization; otherwise the user-facing reason
    /// <paramref name="fileName"/> must not be launched.
    /// </summary>
    public static string? SubjectProblem(string subject, string fileName)
    {
        string? organization;
        try
        {
            organization = OrganizationOf(subject);
        }
        catch (CryptographicException)
        {
            return $"{fileName}'s signer has a subject that could not be read (\"{subject}\"). {ChooseClaude}";
        }
        if (organization is null)
        {
            return $"{fileName} is signed by \"{subject}\", which names no organization. {ChooseClaude}";
        }
        if (!ExpectedOrganizations.Contains(NormalizeOrganization(organization), StringComparer.Ordinal))
        {
            return $"{fileName} is signed by \"{subject}\", not by Anthropic. {ChooseClaude}";
        }
        return null;
    }

    /// <summary>The value of the first single-valued O= (id-at-organizationName, 2.5.4.10) attribute, or null.</summary>
    internal static string? OrganizationOf(string subject)
    {
        var name = new X500DistinguishedName(subject);
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.10")
            {
                return rdn.GetSingleElementValue();
            }
        }
        return null;
    }

    /// <summary>Lower-case, punctuation dropped, whitespace collapsed: "Anthropic, PBC" and "ANTHROPIC P.B.C." compare equal.</summary>
    internal static string NormalizeOrganization(string organization)
    {
        var normalized = new StringBuilder(organization.Length);
        var pendingSpace = false;
        foreach (var c in organization)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }
                pendingSpace = false;
                normalized.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
        }
        return normalized.ToString();
    }
}
