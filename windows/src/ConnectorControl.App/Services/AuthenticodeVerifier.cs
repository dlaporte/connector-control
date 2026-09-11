using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ConnectorControl.Core;

namespace ConnectorControl.App.Services;

/// <summary>
/// The Windows half of the Mac ClaudeRestarter's SecStaticCode check. A legacy
/// (exe-path) launch target — detected, or the string the user chose under
/// Settings ▸ Claude, which lives in settings.json — is launched by this app
/// as a child process. Before Claude is quit and that path started, the file
/// has to carry a valid Authenticode signature chained to a trusted root
/// (WinVerifyTrust, no UI, no network) AND name Anthropic as the signing
/// organization (<see cref="ClaudePublisher"/>). An MSIX launch target is an
/// app identity that Windows itself verified at install, so it never comes here.
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// Null when <paramref name="exePath"/> is validly signed by Anthropic; otherwise the
    /// user-facing reason it must not be launched. Reads the whole file: call it off the UI thread
    /// where a stall would show.
    /// </summary>
    public static string? VerifyClaude(string exePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var name = Path.GetFileName(exePath);
        var signer = SignerSubject(exePath);
        if (signer.Problem is not null)
        {
            return signer.Problem + " " + ClaudePublisher.ChooseClaude;
        }
        return ClaudePublisher.SubjectProblem(signer.Identity!, name);
    }

    /// <summary>
    /// <see cref="Unsigned"/> is TRUST_E_NOSIGNATURE — the file carries no Authenticode signature at
    /// all (a dev build). Every other failure (untrusted root, tampered file, unreadable signer)
    /// is a <see cref="Problem"/> with a null <see cref="Identity"/>; a null Problem always carries one.
    /// </summary>
    public sealed record SignerResult(SignerIdentity? Identity, string? Problem, bool Unsigned);

    private const int TrustENoSignature = unchecked((int)0x800B0100);

    /// <summary>
    /// The identity of <paramref name="exePath"/>'s signer once WinVerifyTrust has accepted the
    /// signature and its chain; otherwise a user-facing problem naming the file. Shared by the
    /// Claude launch check and the update-package check.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SignerResult SignerSubject(string exePath)
    {
        var name = Path.GetFileName(exePath);
        var status = VerifyTrust(exePath);
        if (status != 0)
        {
            return new SignerResult(null, $"{name} does not carry a valid code signature (0x{status:X8}).", status == TrustENoSignature);
        }
        try
        {
            // SYSLIB0057 points at X509CertificateLoader, which loads certificate files, not the
            // signer of an Authenticode-signed executable; CreateFromSignedFile is still the only
            // framework call for that, and WinVerifyTrust above has already validated the chain.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(exePath);
#pragma warning restore SYSLIB0057
            return new SignerResult(SignerIdentity.Parse(certificate.Subject), null, false);
        }
        catch (CryptographicException ex)
        {
            return new SignerResult(null, $"{name}'s signer could not be read ({ex.Message}).", false);
        }
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    // DllImport rather than LibraryImport: the source generator needs AllowUnsafeBlocks, which
    // nothing else in the app wants, and these three arguments are all blittable anyway.
    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, in Guid actionId, IntPtr data);

    /// <summary>0 when the file's Authenticode signature is valid and chains to a trusted root.</summary>
    [SupportedOSPlatform("windows")]
    private static int VerifyTrust(string path)
    {
        var filePath = IntPtr.Zero;
        var fileInfo = IntPtr.Zero;
        var data = IntPtr.Zero;
        try
        {
            filePath = Marshal.StringToHGlobalUni(Path.GetFullPath(path));
            var info = new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = filePath,
            };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(info, fileInfo, fDeleteOld: false);
            var trust = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfo,
                dwStateAction = WtdStateActionIgnore,
                // No revocation fetch: a restart click must not wait on the network, and a
                // revoked Anthropic certificate is not the threat here (a stranger's exe is).
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };
            data = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());
            Marshal.StructureToPtr(trust, data, fDeleteOld: false);
            return WinVerifyTrust(new IntPtr(-1), in GenericVerifyV2, data);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(data);
            }
            if (fileInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfo);
            }
            if (filePath != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(filePath);
            }
        }
    }
}
