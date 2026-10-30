namespace ConnectorControl.Core.Services;

/// <summary>
/// A downloaded update failed authenticity verification - its binaries are not signed by this
/// app's own publisher - and was not staged. Unlike a network failure this is announced even
/// from a background check: it means the release feed served something the signing identity
/// did not produce. The message is user-facing.
/// </summary>
public sealed class UpdateVerificationException(string message) : Exception(message)
{
    /// <summary>True when the refused package's Update.exe could not be replaced by the one that was there before
    /// the download, so the copy saved for that must be kept and put back later.</summary>
    public bool UpdaterLeftInPlace { get; init; }
}
