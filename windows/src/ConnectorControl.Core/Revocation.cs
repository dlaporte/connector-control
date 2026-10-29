namespace ConnectorControl.Core;

/// <summary>
/// Windows only: the Mac's updates are judged by Sparkle's own key, with no certificate revocation in play.
///
/// What a WinVerifyTrust result with revocation checking on means for an update. A certificate known to be revoked
/// refuses the update; a revocation status that cannot be determined — the servers out of reach, a timeout, a
/// certificate with no revocation information — does not: the update is then judged as it would be with revocation
/// off, and the caller logs that it was. An update must never be refused only because a network is down or a
/// revocation server is slow; refusing would leave an offline machine on an old build, with no say in it.
/// </summary>
public static class Revocation
{
    public enum Outcome
    {
        /// <summary>The signature and its chain are valid, and nothing in the chain is revoked.</summary>
        Trusted,
        /// <summary>Revoked, or invalid for any reason that has nothing to do with reaching a server.</summary>
        Refused,
        /// <summary>Valid as far as could be checked, but whether it is revoked could not be found out.</summary>
        Undetermined,
    }

    /// <summary>CRYPT_E_NO_REVOCATION_CHECK: the revocation function could not check the certificate.</summary>
    public const int NoRevocationCheck = unchecked((int)0x80092012);

    /// <summary>CRYPT_E_REVOCATION_OFFLINE: the revocation server was offline or did not answer in time.</summary>
    public const int RevocationOffline = unchecked((int)0x80092013);

    /// <summary>CERT_E_REVOCATION_FAILURE: what WinVerifyTrust itself returns when a chain's revocation could not be
    /// checked — the two codes above are what the chain engine reports underneath it.</summary>
    public const int RevocationFailure = unchecked((int)0x800B010E);

    /// <summary>The outcome of WinVerifyTrust's <paramref name="status"/>. Only the three "could not find out" codes are
    /// <see cref="Outcome.Undetermined"/>; CERT_E_REVOKED and CRYPT_E_REVOKED, like every other failure, refuse.</summary>
    public static Outcome Classify(int status) => status switch
    {
        0 => Outcome.Trusted,
        NoRevocationCheck or RevocationOffline or RevocationFailure => Outcome.Undetermined,
        _ => Outcome.Refused,
    };
}
