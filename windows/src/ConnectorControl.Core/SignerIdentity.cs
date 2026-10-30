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
    /// The value of the O= (id-at-organizationName, 2.5.4.10) attribute - exactly one, or null. Two
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

    /// <summary>
    /// Whether two subject DNs name the same signer: the same attributes, with the same values, in the same order -
    /// however each string spells or quotes them. Windows only in use: the update check holds every file that must be
    /// this app's own to the running app's whole subject, not just its O=, which another certificate could share.
    /// A subject that is not a distinguished name, or names nothing, matches nothing.
    /// </summary>
    public static bool SameSubject(string one, string other) =>
        Attributes(one) is { Count: > 0 } a && Attributes(other) is { } b && a.SequenceEqual(b);

    /// <summary>Each RDN of <paramref name="subject"/> as (type, value); a multi-valued one by its encoding. Null when unparseable.</summary>
    private static List<(string Type, string? Value)>? Attributes(string subject)
    {
        try
        {
            return new X500DistinguishedName(subject).EnumerateRelativeDistinguishedNames()
                .Select(rdn => rdn.HasMultipleElements
                    ? ("multi", Convert.ToHexString(rdn.RawData.Span))
                    : (rdn.GetSingleElementType().Value ?? "", rdn.GetSingleElementValue()))
                .ToList();
        }
        catch (CryptographicException)
        {
            return null;
        }
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
