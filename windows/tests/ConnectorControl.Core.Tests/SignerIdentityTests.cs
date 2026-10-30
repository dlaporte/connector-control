namespace ConnectorControl.Core.Tests;

public class SignerIdentityTests
{
    [Theory]
    [InlineData("Anthropic, PBC", "anthropic pbc")]
    [InlineData("ANTHROPIC   P.B.C.", "anthropic pbc")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("  Anthropic  ", "anthropic")]
    public void NormalizationFoldsCasePunctuationAndSpacing(string organization, string expected)
    {
        Assert.Equal(expected, SignerIdentity.NormalizeOrganization(organization));
    }

    [Fact]
    public void OrganizationIsTheSoleOAttribute()
    {
        Assert.Equal("Anthropic, PBC", SignerIdentity.OrganizationOf("CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", C=US"));
        Assert.Null(SignerIdentity.OrganizationOf("CN=Anthropic, OU=Anthropic"));
        Assert.Null(SignerIdentity.OrganizationOf("O=Anthropic, O=Evil Corp"));   // two organizations is nobody's identity
        Assert.Null(SignerIdentity.OrganizationOf("not a distinguished name at all"));
    }

    [Fact]
    public void ParseKeepsTheSubjectAndNormalizesTheOrganization()
    {
        var subject = "CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", C=US";
        Assert.Equal(new SignerIdentity(subject, "anthropic pbc"), SignerIdentity.Parse(subject));
        Assert.Null(SignerIdentity.Parse("CN=Anthropic").Organization);
    }

    /// <summary>An update must come from this app's own certificate subject, every attribute of it, not merely a
    /// certificate naming the same organization. Spelling aside: the same attributes in the same order match.</summary>
    [Fact]
    public void SubjectsMatchAttributeForAttribute()
    {
        const string ours = "CN=David LaPorte, O=David LaPorte, L=Boston, S=Massachusetts, C=US";
        Assert.True(SignerIdentity.SameSubject(ours, "CN=David LaPorte,O=David LaPorte,L=Boston,S=Massachusetts,C=US"));
        Assert.True(SignerIdentity.SameSubject("CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", C=US", "CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", C=US"));
        Assert.False(SignerIdentity.SameSubject(ours, "CN=Someone Else, O=David LaPorte, L=Boston, S=Massachusetts, C=US"));
        Assert.False(SignerIdentity.SameSubject(ours, "CN=David LaPorte, O=David LaPorte, C=US"));
        Assert.False(SignerIdentity.SameSubject(ours, "C=US, S=Massachusetts, L=Boston, O=David LaPorte, CN=David LaPorte"));
        Assert.False(SignerIdentity.SameSubject(ours, "CN=David LaPorte, O=David LaPorte, L=Boston, S=Massachusetts, C=US, OU=Extra"));
        Assert.False(SignerIdentity.SameSubject("not a distinguished name at all", "not a distinguished name at all"));
        Assert.False(SignerIdentity.SameSubject("", ""));
    }
}
