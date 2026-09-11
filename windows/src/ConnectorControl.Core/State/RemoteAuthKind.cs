namespace ConnectorControl.Core.State;

/// <summary>The four ways the Remote form can authenticate, in picker order.</summary>
public enum RemoteAuthKind
{
    Automatic,
    Bearer,
    Header,
    OAuthClient,
}

public static class RemoteAuthKindExtensions
{
    /// <summary>The picker's label for this kind.</summary>
    public static string Title(this RemoteAuthKind kind) => kind switch
    {
        RemoteAuthKind.Automatic => "Automatic (OAuth / none)",
        RemoteAuthKind.Bearer => "Bearer token",
        RemoteAuthKind.Header => "Custom header",
        RemoteAuthKind.OAuthClient => "OAuth client ID/secret",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
