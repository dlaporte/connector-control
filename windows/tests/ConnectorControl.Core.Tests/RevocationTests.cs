namespace ConnectorControl.Core.Tests;

/// <summary>Windows only, no Mac counterpart: the Mac's updates are judged by Sparkle's own key. The decision is
/// plain arithmetic on WinVerifyTrust's result, so it runs on any platform.</summary>
public class RevocationTests
{
    [Theory]
    [InlineData(0, Revocation.Outcome.Trusted)]
    [InlineData(unchecked((int)0x800B010C), Revocation.Outcome.Refused)]       // CERT_E_REVOKED
    [InlineData(unchecked((int)0x80092010), Revocation.Outcome.Refused)]       // CRYPT_E_REVOKED
    [InlineData(unchecked((int)0x80092013), Revocation.Outcome.Undetermined)]  // CRYPT_E_REVOCATION_OFFLINE: servers out of reach, or timed out
    [InlineData(unchecked((int)0x80092012), Revocation.Outcome.Undetermined)]  // CRYPT_E_NO_REVOCATION_CHECK: no revocation information
    [InlineData(unchecked((int)0x800B010E), Revocation.Outcome.Undetermined)]  // CERT_E_REVOCATION_FAILURE: WinVerifyTrust's own word for either
    [InlineData(unchecked((int)0x800B0100), Revocation.Outcome.Refused)]       // TRUST_E_NOSIGNATURE
    [InlineData(unchecked((int)0x80096010), Revocation.Outcome.Refused)]       // TRUST_E_BAD_DIGEST: a tampered file
    [InlineData(unchecked((int)0x800B0109), Revocation.Outcome.Refused)]       // CERT_E_UNTRUSTEDROOT
    public void OnlyARevocationThatCannotBeDeterminedFallsBack(int status, Revocation.Outcome expected)
    {
        Assert.Equal(expected, Revocation.Classify(status));
    }
}
