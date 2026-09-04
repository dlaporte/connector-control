namespace ConnectorControl.Core.State;

/// <summary>The four ways the Remote form can authenticate (catalog §3.2), in picker order.</summary>
public enum RemoteAuthKind
{
    Automatic,
    Bearer,
    Header,
    OAuthClient,
}
