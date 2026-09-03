namespace ConnectorControl.Core;

/// <summary>How mcp-remote should authenticate to the remote server (Swift <c>RemoteAuth</c>).</summary>
public abstract record RemoteAuth
{
    private RemoteAuth()
    {
    }

    /// <summary>OAuth dynamic client registration, or no auth for an open server.</summary>
    public sealed record Automatic : RemoteAuth;

    /// <summary><c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    public sealed record Bearer(string Token) : RemoteAuth;

    /// <summary>An arbitrary static header.</summary>
    public sealed record Header(string Name, string Value) : RemoteAuth;

    /// <summary>A pre-registered OAuth client (id/secret, optional space-separated scopes).</summary>
    public sealed record OAuthClient(string ClientId, string ClientSecret, string Scopes) : RemoteAuth;

    /// <summary>The shared <see cref="Automatic"/> value (all instances are equal anyway).</summary>
    public static RemoteAuth Auto { get; } = new Automatic();
}
