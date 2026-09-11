using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// executable inside the package (found by content, not by name) must be validly signed —
/// this app's own files and the updater by the same organization as the running app, the
/// runtime and libraries it is built from by that organization or one in
/// <see cref="TrustedOrganizations"/> — and the package's own ConnectorControl.exe must
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

    /// <summary>
    /// Publishers whose signatures the package legitimately carries besides this app's own: the
    /// .NET runtime and the WPF stack are Microsoft-signed, and one toolkit is signed by the .NET
    /// Foundation. vpk signs what ships unsigned (this app's assemblies, the updater, plain NuGet
    /// libraries) and leaves already-signed files alone. Normalized like <see cref="SignerIdentity.Organization"/>.
    /// The smoke test applies the same list, so a release cannot ship a package a client would refuse.
    /// </summary>
    public static readonly string[] TrustedOrganizations = ["microsoft corporation", "windows community toolkit net foundation"];

    /// <summary>
    /// The files an attacker would have to replace to run code in this app's name: its own
    /// executables and assemblies, and the updater. These must carry this app's own signature;
    /// a trusted third party's is not enough.
    /// </summary>
    internal static bool MustBeOurs(string fileName) =>
        fileName.StartsWith("ConnectorControl", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "Squirrel.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "Update.exe", StringComparison.OrdinalIgnoreCase);

    private const string CannotValidateSelf = "This app's own signature could not be validated, so an update cannot be checked against it";

    /// <summary>What the running app's signature says about who may sign an update; read once per download.</summary>
    public readonly record struct RunningIdentity(string? Organization, bool Unsigned);

    /// <summary>
    /// Null when the update may be staged; otherwise a user-facing reason. Only an UNSIGNED running
    /// app (a dev build) skips the check — it has no identity to compare against and the updater is
    /// inert there anyway. A running app whose signature cannot be validated refuses: that is the
    /// machine, not the build, and an unverifiable identity must not become "accept everything".
    /// </summary>
    public static string? Verify(string packagePath, string? updateExePath, RunningIdentity identity, Version runningVersion, Version feedVersion)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        if (!TryExpectedOrganization(identity, out var expected, out var organizationProblem))
        {
            return organizationProblem;
        }
        if (updateExePath is not null && File.Exists(updateExePath)
            && VerifyFile(updateExePath, expected, "Update.exe", mustBeOurs: true) is { } updaterProblem)
        {
            return updaterProblem;
        }
        var scratch = Directory.CreateTempSubdirectory("cc-update-");
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            var magic = new byte[2];
            var buffer = new byte[81920];
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
                // Every entry is inflated in full: the declared size in the zip's directory is the
                // attacker's to write, so a program hidden behind a declared size of zero is found
                // by what actually inflates, not by what the directory claims. Only a program is
                // written to disk, where the signature check needs it; anything else is counted.
                var name = Path.GetFileName(entry.FullName);
                using var content = entry.Open();
                var magicLength = ReadMagic(content, magic);
                if (!IsMZ(magic.AsSpan(0, magicLength)))
                {
                    var counted = magicLength + CountBytes(content, buffer);
                    if (counted != entry.Length)
                    {
                        return SizeProblem(entry, counted);
                    }
                    continue;
                }
                var extracted = Path.Combine(scratch.FullName, $"{Guid.NewGuid():N}-{name}");
                using (var file = File.Create(extracted))
                {
                    file.Write(magic, 0, magicLength);
                    content.CopyTo(file);
                }
                var actual = new FileInfo(extracted).Length;
                if (actual != entry.Length)
                {
                    return SizeProblem(entry, actual);
                }
                if (VerifyFile(extracted, expected, name + " inside the update", MustBeOurs(name)) is { } problem)
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
            FileSystemErrors.TryDelete(scratch.FullName);
        }
    }

    /// <summary>
    /// Before anything is downloaded: Velopack would run the INSTALLED Update.exe to apply a delta,
    /// so it must already be ours. Null when it is (or when there is no identity to compare against).
    /// </summary>
    public static string? VerifyInstalledUpdater(string? updateExePath, RunningIdentity identity)
    {
        if (!OperatingSystem.IsWindows() || updateExePath is null || !File.Exists(updateExePath))
        {
            return null;
        }
        if (!TryExpectedOrganization(identity, out var expected, out var problem))
        {
            return problem;
        }
        return VerifyFile(updateExePath, expected, "The installed Update.exe", mustBeOurs: true);
    }

    /// <summary>
    /// True when there is an organization to check a file's signer against; <paramref name="expected"/>
    /// carries it. False and <paramref name="expected"/> null either because the running app is
    /// unsigned (a dev build: <paramref name="problem"/> is also null, meaning skip every check) or
    /// because its own signature could not be validated (<paramref name="problem"/> carries why).
    /// </summary>
    private static bool TryExpectedOrganization(RunningIdentity identity, [NotNullWhen(true)] out string? expected, out string? problem)
    {
        if (identity.Unsigned)
        {
            expected = null;
            problem = null;
            return false;
        }
        if (identity.Organization is not { } organization)
        {
            expected = null;
            problem = CannotValidateSelf + NotInstalledSuffix;
            return false;
        }
        expected = organization;
        problem = null;
        return true;
    }

    /// <summary>The running app's signer organization, normalized; <c>Unsigned</c> when it carries no signature at all.</summary>
    [SupportedOSPlatform("windows")]
    internal static RunningIdentity ExpectedIdentity(string runningExePath)
    {
        var signer = AuthenticodeVerifier.SignerSubject(runningExePath);
        return new RunningIdentity(signer.Identity?.Organization, signer.Unsigned);
    }

    /// <summary>Null when <paramref name="path"/> is validly signed by <paramref name="expectedOrganization"/>; else why not.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? VerifyFile(string path, string expectedOrganization, string displayName, bool mustBeOurs)
    {
        var signer = AuthenticodeVerifier.SignerSubject(path);
        if (signer.Identity is not { } identity)
        {
            return $"{displayName} is not validly signed{NotInstalledSuffix}";
        }
        if (identity.Organization == expectedOrganization)
        {
            return null;
        }
        if (mustBeOurs)
        {
            return $"{displayName} is signed by \"{identity.Subject}\", not by this app's publisher{NotInstalledSuffix}";
        }
        if (identity.Organization is { } organization && TrustedOrganizations.Contains(organization, StringComparer.Ordinal))
        {
            return null;
        }
        return $"{displayName} is signed by \"{identity.Subject}\", not by this app's publisher or one it is built from{NotInstalledSuffix}";
    }

    /// <summary>
    /// The version baked into the signed executable is the one the attacker cannot rewrite: it must
    /// equal what the feed advertises (no relabeling an old release as new) and must not be older
    /// than what is running (no downgrade to a build without this check). <paramref name="runningVersion"/>
    /// and <paramref name="feedVersion"/> are already major.minor.patch (<see cref="VelopackUpdater"/>'s
    /// own NumericVersion normalizes them before this is ever called).
    /// </summary>
    internal static string? VersionProblem(string mainExecutablePath, Version runningVersion, Version feedVersion)
    {
        var embedded = EmbeddedVersion(mainExecutablePath);
        if (embedded != feedVersion)
        {
            return $"{MainExecutable} inside the update is version {embedded}, but the update feed says {feedVersion}{NotInstalledSuffix}";
        }
        if (embedded < runningVersion)
        {
            return $"{MainExecutable} inside the update is version {embedded}, older than the installed {runningVersion}{NotInstalledSuffix}";
        }
        return null;
    }

    /// <summary>The file version baked into a signed executable, major.minor.build.</summary>
    internal static Version EmbeddedVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    private static string SizeProblem(ZipArchiveEntry entry, long actual) =>
        $"The update package entry \"{entry.FullName}\" declares {entry.Length} bytes but holds {actual}{NotInstalledSuffix}";

    /// <summary>Reads up to the first two bytes of <paramref name="stream"/> into <paramref name="magic"/>; a shorter stream returns the shorter count.</summary>
    private static int ReadMagic(Stream stream, Span<byte> magic) => stream.ReadAtLeast(magic, 2, throwOnEndOfStream: false);

    /// <summary>
    /// By content, not name: every Windows executable starts with "MZ". A .dll that is not one cannot
    /// be loaded; a program under any other name still can, and Velopack's extractor lets Windows
    /// trim a trailing dot from a name, so "ConnectorControl.exe." lands as the real thing.
    /// The two bytes every Windows executable starts with; a shorter read is not one.
    /// </summary>
    internal static bool IsMZ(ReadOnlySpan<byte> magic) => magic.Length == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';

    /// <summary>Inflates the rest of <paramref name="stream"/> without keeping it: what the entry actually holds.</summary>
    private static long CountBytes(Stream stream, byte[] buffer)
    {
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
        }
        return total;
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
