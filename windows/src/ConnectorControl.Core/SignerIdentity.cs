using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ConnectorControl.Core;

/// <summary>An Authenticode signer as this app judges it: the subject DN and the one organization (O=) it names, normalized.</summary>
public sealed record SignerIdentity(string Subject, string? Organization)
{
    /// <summary>Null Organization when the DN is unparseable, names no O=, or names more than one.</summary>
    public static SignerIdentity Parse(string subject)
    {
        var organization = OrganizationOf(subject);
        return new SignerIdentity(subject, organization is null ? null : NormalizeOrganization(organization));
    }

    /// <summary>
    /// The value of the O= (id-at-organizationName, 2.5.4.10) attribute — exactly one, or null. Two
    /// organizations is nobody's identity, and a subject that is not a distinguished name names none.
    /// </summary>
    internal static string? OrganizationOf(string subject)
    {
        X500DistinguishedName name;
        try
        {
            name = new X500DistinguishedName(subject);
        }
        catch (CryptographicException)
        {
            return null;
        }
        string? organization = null;
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.10")
            {
                if (organization is not null)
                {
                    return null;
                }
                organization = rdn.GetSingleElementValue();
            }
        }
        return organization;
    }

    /// <summary>Lower-case, punctuation dropped, whitespace collapsed: "Anthropic, PBC" and "ANTHROPIC P.B.C." compare equal.</summary>
    public static string NormalizeOrganization(string organization)
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
