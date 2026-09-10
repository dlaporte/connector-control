using System.Diagnostics;
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
/// executable inside the package (found by content, not by name) must be validly signed by
/// the same organization as the running app, and the package's own ConnectorControl.exe must
/// carry the version the feed advertises, so an old signed release cannot be replayed as new.
/// Sparkle's pinned EdDSA key plays this role on the Mac. Known residual: any library this
/// publisher ever signed is interchangeable with any other inside a package; only the main
/// executable is bound to a version.
/// </summary>
public static class UpdateVerifier
{
    public const string NotInstalledSuffix = "; the update was not installed.";

    /// <summary>The binary whose embedded file version must match the feed; every package carries it.</summary>
    public const string MainExecutable = "ConnectorControl.exe";

    private const string CannotValidateSelf = "This app's own signature could not be validated, so an update cannot be checked against it";

    /// <summary>What the running app's signature says about who may sign an update.</summary>
    internal readonly record struct RunningIdentity(string? Organization, bool Unsigned);

    /// <summary>
    /// Null when the update may be staged; otherwise a user-facing reason. Only an UNSIGNED running
    /// app (a dev build) skips the check — it has no identity to compare against and the updater is
    /// inert there anyway. A running app whose signature cannot be validated refuses: that is the
    /// machine, not the build, and an unverifiable identity must not become "accept everything".
    /// </summary>
    public static string? Verify(string packagePath, string? updateExePath, string runningExePath, Version runningVersion, Version feedVersion)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var identity = ExpectedIdentity(runningExePath);
        if (identity.Unsigned)
        {
            return null;
        }
        if (identity.Organization is not { } expected)
        {
            return CannotValidateSelf + NotInstalledSuffix;
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
            var index = 0;
            string? mainExecutable = null;
            foreach (var entry in zip.Entries)
            {
                if (IsSuspiciousEntryName(entry.FullName))
                {
                    return $"The update package contains an entry named \"{entry.FullName}\", which Windows would rename on extraction{NotInstalledSuffix}";
                }
                if (entry.FullName.EndsWith('/'))
                {
                    continue;   // a directory entry
                }
                // Every entry is unpacked in full: the declared size in the zip's directory is the
                // attacker's to write, so a program hidden behind a declared size of zero is found
                // by what actually inflates, not by what the directory claims.
                var name = Path.GetFileName(entry.FullName);
                var extracted = Path.Combine(scratch.FullName, $"{index++}-{name}");
                entry.ExtractToFile(extracted, overwrite: true);
                var actual = new FileInfo(extracted).Length;
                if (actual != entry.Length)
                {
                    return $"The update package entry \"{entry.FullName}\" declares {entry.Length} bytes but holds {actual}{NotInstalledSuffix}";
                }
                if (!IsPortableExecutable(extracted))
                {
                    continue;
                }
                if (VerifyFile(extracted, expected, name + " inside the update") is { } problem)
                {
                    return problem;
                }
                if (string.Equals(name, MainExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    if (mainExecutable is not null)
                    {
                        return $"The update package contains {MainExecutable} more than once{NotInstalledSuffix}";
                    }
                    mainExecutable = extracted;
                }
            }
            if (mainExecutable is null)
            {
                // "Nothing failed" is not "everything passed": a real package always carries the app.
                return $"The update package does not contain {MainExecutable}{NotInstalledSuffix}";
            }
            return VersionProblem(mainExecutable, runningVersion, feedVersion);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            // Any other exception propagates; the coordinator treats that as a failed download too. Closed either way.
            return $"The update package could not be read ({ex.Message}){NotInstalledSuffix}";
        }
        finally
        {
            try { scratch.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Before anything is downloaded: Velopack would run the INSTALLED Update.exe to apply a delta,
    /// so it must already be ours. Null when it is (or when there is no identity to compare against).
    /// </summary>
    public static string? VerifyInstalledUpdater(string? updateExePath, string runningExePath)
    {
        if (!OperatingSystem.IsWindows() || updateExePath is null || !File.Exists(updateExePath))
        {
            return null;
        }
        var identity = ExpectedIdentity(runningExePath);
        if (identity.Unsigned)
        {
            return null;
        }
        if (identity.Organization is not { } expected)
        {
            return CannotValidateSelf + NotInstalledSuffix;
        }
        return VerifyFile(updateExePath, expected, "The installed Update.exe");
    }

    /// <summary>The running app's signer organization, normalized; <c>Unsigned</c> when it carries no signature at all.</summary>
    [SupportedOSPlatform("windows")]
    internal static RunningIdentity ExpectedIdentity(string runningExePath)
    {
        var signer = AuthenticodeVerifier.SignerSubject(runningExePath);
        if (signer.Unsigned)
        {
            return new RunningIdentity(null, true);
        }
        if (signer.Problem is not null || signer.Subject is null)
        {
            return new RunningIdentity(null, false);
        }
        string? organization;
        try
        {
            organization = ClaudePublisher.OrganizationOf(signer.Subject);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            organization = null;
        }
        return new RunningIdentity(organization is null ? null : ClaudePublisher.NormalizeOrganization(organization), false);
    }

    /// <summary>Null when <paramref name="path"/> is validly signed by <paramref name="expectedOrganization"/>; else why not.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? VerifyFile(string path, string expectedOrganization, string displayName)
    {
        var signer = AuthenticodeVerifier.SignerSubject(path);
        if (signer.Problem is not null || signer.Subject is null)
        {
            return $"{displayName} is not validly signed{NotInstalledSuffix}";
        }
        string? organization;
        try
        {
            organization = ClaudePublisher.OrganizationOf(signer.Subject);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            organization = null;
        }
        if (organization is null || ClaudePublisher.NormalizeOrganization(organization) != expectedOrganization)
        {
            return $"{displayName} is signed by \"{signer.Subject}\", not by this app's publisher{NotInstalledSuffix}";
        }
        return null;
    }

    /// <summary>
    /// The version baked into the signed executable is the one the attacker cannot rewrite: it must
    /// equal what the feed advertises (no relabeling an old release as new) and must not be older
    /// than what is running (no downgrade to a build without this check).
    /// </summary>
    internal static string? VersionProblem(string mainExecutablePath, Version runningVersion, Version feedVersion)
    {
        var info = FileVersionInfo.GetVersionInfo(mainExecutablePath);
        var embedded = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        var feed = Numeric(feedVersion);
        var running = Numeric(runningVersion);
        if (embedded != feed)
        {
            return $"{MainExecutable} inside the update is version {embedded}, but the update feed says {feed}{NotInstalledSuffix}";
        }
        if (embedded < running)
        {
            return $"{MainExecutable} inside the update is version {embedded}, older than the installed {running}{NotInstalledSuffix}";
        }
        return null;
    }

    /// <summary>Major.minor.patch only: previews share the numeric part of the release they lead to.</summary>
    internal static Version Numeric(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>
    /// By content, not name: every Windows executable starts with "MZ". A .dll that is not one cannot
    /// be loaded; a program under any other name still can, and Velopack's extractor lets Windows
    /// trim a trailing dot from a name, so "ConnectorControl.exe." lands as the real thing.
    /// </summary>
    internal static bool IsPortableExecutable(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();   // the inflated bytes, whatever the directory declares
        return StartsWithMZ(stream);
    }

    /// <summary>The extracted-file form of <see cref="IsPortableExecutable(ZipArchiveEntry)"/>.</summary>
    internal static bool IsPortableExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        return StartsWithMZ(stream);
    }

    private static bool StartsWithMZ(Stream stream)
    {
        var header = new byte[2];
        var read = stream.ReadAtLeast(header, 2, throwOnEndOfStream: false);
        return read == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }

    /// <summary>A path segment Windows would rewrite on extraction (trailing dot or space), an NTFS stream (colon), or a walk upward.</summary>
    internal static bool IsSuspiciousEntryName(string entryName)
    {
        foreach (var segment in entryName.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return true;
            }
            if (segment.Length > 0 && (segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':')))
            {
                return true;
            }
        }
        return false;
    }
}
