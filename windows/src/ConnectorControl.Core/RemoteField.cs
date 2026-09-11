namespace ConnectorControl.Core;

/// <summary>
/// A Remote-form field whose value <see cref="RemotePattern.Encode"/> places in <c>args</c>, where cmd.exe
/// re-parses it under <see cref="RemoteLaunchStyle.CmdNpx"/>; see <see cref="RemotePattern.CmdUnsafeField"/>.
/// </summary>
public enum RemoteField
{
    Url,
    HeaderName,
    ClientId,
    ClientSecret,
    Scopes,
}
