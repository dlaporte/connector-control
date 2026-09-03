namespace ConnectorControl.Core;

/// <summary><c>%LOCALAPPDATA%</c> and <c>%APPDATA%</c>, injectable for tests.</summary>
public sealed record KnownFolders(string LocalAppData, string RoamingAppData)
{
    public static KnownFolders Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}
