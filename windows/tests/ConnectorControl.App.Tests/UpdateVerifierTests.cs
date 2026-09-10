using System.IO.Compression;
using ConnectorControl.App.Services;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class UpdateVerifierTests : IDisposable
{
    private readonly TempDir dir = new("updver");

    public void Dispose() => dir.Dispose();

    /// <summary>The test host (testhost.exe) is Microsoft-signed on the CI runner; the test assembly is not signed at all.</summary>
    private static string RunningExe => Environment.ProcessPath!;
    private static string UnsignedBinary => typeof(UpdateVerifierTests).Assembly.Location;
    private static string MicrosoftSignedBinary => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

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

    private static void SkipUnlessHostIsSigned()
    {
        if (UpdateVerifier.ExpectedOrganization(RunningExe) is null)
        {
            Assert.Skip("the test host is not Authenticode-signed here");
        }
    }

    [Fact]
    public void ABinaryFromAnotherPublisherIsRefused()
    {
        SkipUnlessHostIsSigned();
        var problem = UpdateVerifier.Verify(Package(("lib/app/ConnectorControl.dll", UnsignedBinary)), updateExePath: null, RunningExe);
        Assert.NotNull(problem);
        Assert.Contains("ConnectorControl.dll", problem);
    }

    [Fact]
    public void BinariesFromTheSamePublisherPass()
    {
        SkipUnlessHostIsSigned();
        Assert.Null(UpdateVerifier.Verify(Package(("lib/app/notepad.exe", MicrosoftSignedBinary), ("lib/app/Squirrel.exe", MicrosoftSignedBinary)), updateExePath: MicrosoftSignedBinary, RunningExe));
    }

    [Fact]
    public void AReplacedUpdateExeFromAnotherPublisherIsRefused()
    {
        SkipUnlessHostIsSigned();
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
        SkipUnlessHostIsSigned();
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
