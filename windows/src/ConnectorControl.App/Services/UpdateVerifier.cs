using System.IO.Compression;
using System.Runtime.Versioning;
using ConnectorControl.Core;

namespace ConnectorControl.App.Services;

/// <summary>
/// Velopack checks a downloaded package only against the checksum in the release feed, then
/// overwrites the installed Update.exe with the package's copy and, on apply, starts it. Both
/// come from the same GitHub release, so whoever can write release assets could ship anything.
/// This app's own binaries are Authenticode-signed by its publisher, an identity a release
/// token does not carry: before an update is staged, the replaced Update.exe and every
/// executable inside the package must be validly signed by the same organization as the
/// running app. Sparkle's pinned EdDSA key plays this role on the Mac.
/// </summary>
public static class UpdateVerifier
{
    public const string NotInstalledSuffix = "; the update was not installed.";

    /// <summary>
    /// Null when the update may be staged; otherwise a user-facing reason. An unsigned running
    /// app (a dev build) has no identity to compare against and skips the check — the updater is
    /// inert on such builds anyway.
    /// </summary>
    public static string? Verify(string packagePath, string? updateExePath, string runningExePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var expected = ExpectedOrganization(runningExePath);
        if (expected is null)
        {
            return null;
        }
        if (updateExePath is not null && File.Exists(updateExePath)
            && VerifyFile(updateExePath, expected, "Update.exe") is { } updaterProblem)
        {
            return updaterProblem;
        }
        var scratch = Directory.CreateTempSubdirectory("cc-update-");
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var verified = 0;
            foreach (var entry in zip.Entries)
            {
                if (!IsPortableExecutable(entry.FullName))
                {
                    continue;
                }
                var extracted = Path.Combine(scratch.FullName, Path.GetFileName(entry.FullName));
                entry.ExtractToFile(extracted, overwrite: true);
                if (VerifyFile(extracted, expected, Path.GetFileName(entry.FullName) + " inside the update") is { } problem)
                {
                    return problem;
                }
                verified++;
            }
            // A package with nothing checkable is not "all signed": a real one always carries the app.
            return verified == 0 ? $"The update package contains no programs to verify{NotInstalledSuffix}" : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"The update package could not be read ({ex.Message}){NotInstalledSuffix}";
        }
        finally
        {
            try { scratch.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The running app's signer organization, normalized; null when it is not validly signed.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? ExpectedOrganization(string runningExePath)
    {
        var (subject, problem) = AuthenticodeVerifier.SignerSubject(runningExePath);
        if (problem is not null || subject is null)
        {
            return null;
        }
        string? organization;
        try
        {
            organization = ClaudePublisher.OrganizationOf(subject);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        return organization is null ? null : ClaudePublisher.NormalizeOrganization(organization);
    }

    /// <summary>Null when <paramref name="path"/> is validly signed by <paramref name="expectedOrganization"/>; else why not.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? VerifyFile(string path, string expectedOrganization, string displayName)
    {
        var (subject, problem) = AuthenticodeVerifier.SignerSubject(path);
        if (problem is not null || subject is null)
        {
            return $"{displayName} is not validly signed{NotInstalledSuffix}";
        }
        string? organization;
        try
        {
            organization = ClaudePublisher.OrganizationOf(subject);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            organization = null;
        }
        if (organization is null || ClaudePublisher.NormalizeOrganization(organization) != expectedOrganization)
        {
            return $"{displayName} is signed by \"{subject}\", not by this app's publisher{NotInstalledSuffix}";
        }
        return null;
    }

    /// <summary>Everything Windows would load as code: .exe and .dll, any case.</summary>
    internal static bool IsPortableExecutable(string entryName) =>
        entryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
}
