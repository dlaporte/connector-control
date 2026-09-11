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
}
