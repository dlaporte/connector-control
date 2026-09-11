namespace ConnectorControl.Core.Tests;

public class ClaudePublisherTests
{
    private const string Exe = "claude.exe";

    [Theory]
    [InlineData("CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", L=San Francisco, ST=California, C=US")]
    [InlineData("CN=Anthropic PBC, O=Anthropic PBC, L=San Francisco, ST=California, C=US")]
    [InlineData("CN=Anthropic, O=Anthropic, C=US")]
    [InlineData("O=\"ANTHROPIC, P.B.C.\", CN=Claude")]
    public void AnthropicAsTheOrganizationPasses(string subject)
    {
        Assert.Null(ClaudePublisher.SubjectProblem(subject, Exe));
    }

    [Theory]
    [InlineData("CN=Not Anthropic Ltd, O=Not Anthropic Ltd, C=US")]
    [InlineData("CN=Anthropic Fans, O=Anthropic Fans LLC, C=US")]
    [InlineData("CN=Claude Desktop, OU=anthropic-fans, O=Some Company, C=US")]
    [InlineData("CN=Anthropic")]
    [InlineData("CN=claude.exe, O=Evil Corp, C=US")]
    [InlineData("O=Anthropic, O=Evil Corp, CN=Claude")]
    public void AnyoneElseFails(string subject)
    {
        // Every one of these contains the word or names no organization at all; the old substring check passed the first four.
        Assert.NotNull(ClaudePublisher.SubjectProblem(subject, Exe));
    }

    [Fact]
    public void TheRefusalNamesTheSigner()
    {
        Assert.Equal(
            "claude.exe is signed by \"CN=Evil, O=Evil Corp\", not by Anthropic. Choose the real Claude Desktop under Settings ▸ Claude.",
            ClaudePublisher.SubjectProblem("CN=Evil, O=Evil Corp", Exe));
    }

    [Fact]
    public void ASubjectWithoutAnOrganizationSaysSo()
    {
        Assert.Equal(
            "claude.exe is signed by \"CN=Anthropic\", which names no organization. Choose the real Claude Desktop under Settings ▸ Claude.",
            ClaudePublisher.SubjectProblem("CN=Anthropic", Exe));
    }

    [Fact]
    public void AnUnparseableSubjectFails()
    {
        Assert.NotNull(ClaudePublisher.SubjectProblem("not a distinguished name at all", Exe));
    }
}
