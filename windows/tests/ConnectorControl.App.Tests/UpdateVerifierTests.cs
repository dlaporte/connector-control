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
    /// Microsoft Authenticode signature on every official install (the test host does not, and
    /// Windows system files are catalog-signed, which is not what an update package carries);
    /// the test assembly is not signed at all. CI forbids skips, so a runner whose framework is
    /// unsigned fails loudly with the signer problem in the message.
    /// </summary>
    private static string RunningExe => typeof(object).Assembly.Location;   // System.Private.CoreLib.dll
    private static string MicrosoftSignedBinary => Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll");
    private static string UnsignedBinary => typeof(UpdateVerifierTests).Assembly.Location;

    private string Package(params (string Entry, string Source)[] files)
    {
        var path = dir.File("ConnectorControl-1.9.9-win-x64-full.nupkg");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("lib/app/sq.version");
        foreach (var (entry, source) in files)
        {
            zip.CreateEntryFromFile(source, entry);
        }
        return path;
    }

    private static void RequireSignedFramework()
    {
        var (_, problem) = AuthenticodeVerifier.SignerSubject(RunningExe);
        Assert.True(UpdateVerifier.ExpectedOrganization(RunningExe) is not null, $"the shared framework must be Authenticode-signed for these tests: {problem ?? "no organization in the subject"}");
    }

    [Fact]
    public void ABinaryFromAnotherPublisherIsRefused()
    {
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(Package(("lib/app/ConnectorControl.dll", UnsignedBinary)), updateExePath: null, RunningExe);
        Assert.NotNull(problem);
        Assert.Contains("ConnectorControl.dll", problem);
    }

    [Fact]
    public void BinariesFromTheSamePublisherPass()
    {
        RequireSignedFramework();
        Assert.Null(UpdateVerifier.Verify(Package(("lib/app/notepad.exe", MicrosoftSignedBinary), ("lib/app/Squirrel.exe", MicrosoftSignedBinary)), updateExePath: MicrosoftSignedBinary, RunningExe));
    }

    [Fact]
    public void AReplacedUpdateExeFromAnotherPublisherIsRefused()
    {
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(Package(("lib/app/notepad.exe", MicrosoftSignedBinary)), updateExePath: UnsignedBinary, RunningExe);
        Assert.NotNull(problem);
        Assert.Contains("Update.exe", problem);
    }

    [Fact]
    public void AnUnsignedRunningAppCannotPinAndSkipsVerification()
    {
        // A dev or unsigned preview build has no identity to compare against; the updater is inert there anyway.
        Assert.Null(UpdateVerifier.ExpectedOrganization(UnsignedBinary));
        Assert.Null(UpdateVerifier.Verify(Package(("lib/app/x.dll", UnsignedBinary)), updateExePath: UnsignedBinary, UnsignedBinary));
    }

    [Fact]
    public void APackageWithNoBinariesIsRefused()
    {
        // "Nothing failed" is not "everything passed": a package stripped of every checkable file is refused.
        RequireSignedFramework();
        var problem = UpdateVerifier.Verify(Package(), updateExePath: null, RunningExe);
        Assert.NotNull(problem);
        Assert.Contains("no programs", problem);
    }

    [Theory]
    [InlineData("lib/app/ConnectorControl.exe", true)]
    [InlineData("lib/app/ConnectorControl.dll", true)]
    [InlineData("lib/app/Squirrel.exe", true)]
    [InlineData("lib/app/ConnectorControl.EXE", true)]
    [InlineData("lib/app/sq.version", false)]
    [InlineData("lib/app/ConnectorControl.pdb", false)]
    public void OnlyPortableExecutablesAreChecked(string entry, bool expected)
    {
        Assert.Equal(expected, UpdateVerifier.IsPortableExecutable(entry));
    }
}
