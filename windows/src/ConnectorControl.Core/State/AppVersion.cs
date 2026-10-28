namespace ConnectorControl.Core.State;

/// <summary>
/// Mirror: Sources/ConnectorControlState/AppVersion.swift — its constant only: the Mac's
/// <c>display(info:)</c> reads a bundle's Info.plist, which Windows has none of.
///
/// What a build nobody stamped with a version says it is.
/// </summary>
public static class AppVersion
{
    public const string DevelopmentBuild = "development build";
}
