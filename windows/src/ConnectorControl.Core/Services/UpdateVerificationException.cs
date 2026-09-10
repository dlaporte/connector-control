namespace ConnectorControl.Core.Services;

/// <summary>
/// A downloaded update failed authenticity verification — its binaries are not signed by this
/// app's own publisher — and was not staged. Unlike a network failure this is announced even
/// from a background check: it means the release feed served something the signing identity
/// did not produce. The message is user-facing.
/// </summary>
public sealed class UpdateVerificationException(string message) : Exception(message);
