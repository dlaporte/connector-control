using System.Diagnostics;
using System.IO.Compression;
using ConnectorControl.App.Services;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class UpdateVerifierTests : IDisposable
{
    private readonly TempDir dir = new("updver");

    public void Dispose() => dir.Dispose();

    /// <summary>
    /// Stand-ins with known signatures. The .NET shared framework's DLLs carry an embedded
    /// Microsoft Authenticode signature on every official install; the test assembly is not
    /// signed at all. CI forbids skips, so a runner whose framework is unsigned fails loudly.
    /// </summary>
    private static string RunningExe => typeof(object).Assembly.Location;   // System.Private.CoreLib.dll
    private static string SignedSibling => Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll");
    private static string UnsignedBinary => typeof(UpdateVerifierTests).Assembly.Location;

    /// <summary>The numeric file version baked into the framework DLL that stands in for ConnectorControl.exe.</summary>
    private static Version FrameworkVersion
    {
        get
        {
            var info = FileVersionInfo.GetVersionInfo(RunningExe);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
    }

    private static readonly Version Older = new(1, 0, 0);

    private string Package(params (string Entry, string Source)[] files)
    {
        var path = dir.File($"ConnectorControl-{Guid.NewGuid():N}-win-x64-full.nupkg");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("lib/app/sq.version");
        foreach (var (entry, source) in files)
        {
            zip.CreateEntryFromFile(source, entry);
        }
        return path;
    }

    private string PackageWithBytes(string entry, byte[] bytes)
    {
        var path = dir.File($"ConnectorControl-{Guid.NewGuid():N}-win-x64-full.nupkg");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(RunningExe, "lib/app/ConnectorControl.exe");
        using var stream = zip.CreateEntry(entry).Open();
        stream.Write(bytes);
        return path;
    }

    private static void RequireSignedFramework()
    {
        var signer = AuthenticodeVerifier.SignerSubject(RunningExe);
        Assert.True(UpdateVerifier.ExpectedIdentity(RunningExe).Organization is not null, $"the shared framework must be Authenticode-signed for these tests: {signer.Problem ?? "no organization in the subject"}");
    }

    [Fact]
    public void BinariesFromTheSamePublisherAtTheAdvertisedVersionPass()
    {
        RequireSignedFramework();
        var package = Package(("lib/app/ConnectorControl.exe", RunningExe), ("lib/app/Squirrel.exe", SignedSibling));
        Assert.Null(UpdateVerifier.Verify(package, updateExePath: SignedSibling, RunningExe, Older, FrameworkVersion));
    }

    [Fact]
    public void ABinaryFromAnotherPublisherIsRefused()
    {
        RequireSignedFramework();
        var package = Package(("lib/app/ConnectorControl.exe", RunningExe), ("lib/app/ConnectorControl.dll", UnsignedBinary));
        var problem = UpdateVerifier.Verify(package, updateExePath: null, RunningExe, Older, FrameworkVersion);
        Assert.NotNull(problem);
        Assert.Contains("ConnectorControl.dll", problem);
    }

    [Fact]
    public void AReplacedUpdateExeFromAnotherPublisherIsRefused()
    {
        RequireSignedFramework();
        var package = Package(("lib/app/ConnectorControl.exe", RunningExe));
        var problem = UpdateVerifier.Verify(package, updateExePath: UnsignedBinary, RunningExe, Older, FrameworkVersion);
        Assert.NotNull(problem);
        Assert.Contains("Update.exe", problem);
    }

    [Fact]
    public void APackageWithoutTheMainExecutableIsRefused()
    {
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(Package(("lib/app/Squirrel.exe", SignedSibling)), updateExePath: null, RunningExe, Older, FrameworkVersion);
        Assert.NotNull(problem);
        Assert.Contains(UpdateVerifier.MainExecutable, problem);
    }

    [Fact]
    public void AFeedVersionThatDoesNotMatchTheSignedBinaryIsRefused()
    {
        // The replay: yesterday's signed release relabeled in the feed as tomorrow's.
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(Package(("lib/app/ConnectorControl.exe", RunningExe)), updateExePath: null, RunningExe, Older, new Version(99, 0, 0));
        Assert.NotNull(problem);
        Assert.Contains("update feed says", problem);
    }

    [Fact]
    public void ADowngradeIsRefused()
    {
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(Package(("lib/app/ConnectorControl.exe", RunningExe)), updateExePath: null, RunningExe, new Version(999, 0, 0), FrameworkVersion);
        Assert.NotNull(problem);
        Assert.Contains("older than", problem);
    }

    [Fact]
    public void AnExecutableUnderAnotherNameIsStillChecked()
    {
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(PackageWithBytes("lib/app/notes.txt", File.ReadAllBytes(UnsignedBinary)), updateExePath: null, RunningExe, Older, FrameworkVersion);
        Assert.NotNull(problem);
        Assert.Contains("notes.txt", problem);
    }

    [Fact]
    public void AnUnsignedRunningAppCannotPinAndSkipsVerification()
    {
        // A dev build has no identity to compare against; the updater is inert there anyway.
        Assert.True(UpdateVerifier.ExpectedIdentity(UnsignedBinary).Unsigned);
        Assert.Null(UpdateVerifier.Verify(Package(("lib/app/x.dll", UnsignedBinary)), updateExePath: UnsignedBinary, UnsignedBinary, Older, FrameworkVersion));
        Assert.Null(UpdateVerifier.VerifyInstalledUpdater(UnsignedBinary, UnsignedBinary));
    }

    [Fact]
    public void TheInstalledUpdaterMustBeOursBeforeAnythingIsDownloaded()
    {
        RequireSignedFramework();
        Assert.Null(UpdateVerifier.VerifyInstalledUpdater(SignedSibling, RunningExe));
        Assert.Null(UpdateVerifier.VerifyInstalledUpdater(null, RunningExe));
        var problem = UpdateVerifier.VerifyInstalledUpdater(UnsignedBinary, RunningExe);
        Assert.NotNull(problem);
        Assert.Contains("installed Update.exe", problem);
    }

    [Fact]
    public void ADuplicateMainExecutableIsRefused()
    {
        // Two copies (say, an old signed one and the advertised one) would leave the version check bound to whichever came last.
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(Package(("lib/app/ConnectorControl.exe", RunningExe), ("lib/other/ConnectorControl.exe", RunningExe)), updateExePath: null, RunningExe, Older, FrameworkVersion);
        Assert.NotNull(problem);
        Assert.Contains("more than once", problem);
    }

    [Fact]
    public void NonExecutableEntriesAreUnpackedAndLeftAlone()
    {
        // sq.version and friends are extracted for the size check but never fail signature verification.
        RequireSignedFramework();
        Assert.Null(UpdateVerifier.Verify(PackageWithBytes("lib/app/readme.txt", "hello"u8.ToArray()), updateExePath: null, RunningExe, Older, FrameworkVersion));
    }

    [Theory]
    [InlineData("lib/app/ConnectorControl.exe.", true)]
    [InlineData("lib/app/ConnectorControl.exe ", true)]
    [InlineData("lib/app/a:b.dll", true)]
    [InlineData("lib/../ConnectorControl.exe", true)]
    [InlineData("lib/app/sq.version", false)]
    [InlineData("lib/app/", false)]
    [InlineData("lib/app/ConnectorControl.exe", false)]
    public void SuspiciousEntryNamesAreRefused(string entry, bool expected)
    {
        Assert.Equal(expected, UpdateVerifier.IsSuspiciousEntryName(entry));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00 }, true)]
    [InlineData(new byte[] { (byte)'P', (byte)'K', 0x03, 0x04 }, false)]
    [InlineData(new byte[] { (byte)'M' }, false)]
    public void ExecutablesAreRecognizedByContent(byte[] bytes, bool expected)
    {
        var path = dir.File($"{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("lib/app/anything").Open();
            stream.Write(bytes);
        }
        using var read = ZipFile.OpenRead(path);
        Assert.Equal(expected, UpdateVerifier.IsPortableExecutable(read.Entries.Single()));
    }
}
