using ConnectorControl.App.Services;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

public class PackageVerificationCommandTests : IDisposable
{
    private readonly TempDir dir = new("verifycmd");

    public void Dispose() => dir.Dispose();

    /// <summary>
    /// A real file with a real embedded file version, so <see cref="UpdateVerifier.EmbeddedVersion"/>
    /// has something genuine to read without needing an actual ConnectorControl.exe or a Windows box.
    /// </summary>
    private static string RunningExe => typeof(object).Assembly.Location;

    private string Nupkg(string name)
    {
        var path = dir.File(name);
        File.WriteAllBytes(path, []);   // never opened as a zip: the verify function is stubbed out
        return path;
    }

    private static string? NeverCalled(string packagePath, string? updateExePath, Version runningVersion, Version feedVersion) =>
        throw new InvalidOperationException("verify should not have been called");

    [Theory]
    [InlineData(new object[] { new string[] { } })]
    [InlineData(new object[] { new[] { "-ToastActivated" } })]
    [InlineData(new object[] { new[] { "--report", "out.txt" } })]   // --report alone never triggers this command
    public void UnrelatedArgsAreNotHandled(string[] args)
    {
        Assert.False(PackageVerificationCommand.TryRun(args, NeverCalled, RunningExe, out _));
    }

    [Fact]
    public void MissingReportIsAUsageError()
    {
        var nupkg = Nupkg("ConnectorControl-1.0.0-win-x64-full.nupkg");
        var handled = PackageVerificationCommand.TryRun(["--verify-package", nupkg], NeverCalled, RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void AValuelessFlagIsAUsageError()
    {
        var report = dir.File("report.txt");
        var handled = PackageVerificationCommand.TryRun(["--verify-package"], NeverCalled, RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(report));   // no --report given: exits silently, with nowhere to write the reason
    }

    [Fact]
    public void AValuelessFlagWithAReportPathWritesTheReason()
    {
        var report = dir.File("report.txt");
        // "--verify-package" is the last token here, so it has nothing after it.
        var handled = PackageVerificationCommand.TryRun(["--report", report, "--verify-package"], NeverCalled, RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.Contains("requires a value", File.ReadAllText(report));
    }

    [Fact]
    public void MissingNupkgIsAUsageError()
    {
        var report = dir.File("report.txt");
        var handled = PackageVerificationCommand.TryRun(
            ["--verify-package", dir.File("nowhere.nupkg"), "--report", report],
            NeverCalled, RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.Contains("does not exist", File.ReadAllText(report));
    }

    [Fact]
    public void UnparseableNameIsAUsageError()
    {
        var nupkg = Nupkg("not-a-connector-control-package.nupkg");
        var report = dir.File("report.txt");
        var handled = PackageVerificationCommand.TryRun(["--verify-package", nupkg, "--report", report], NeverCalled, RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(2, exitCode);
    }

    [Theory]
    [InlineData("ConnectorControl-1.3.3-win-x64-full.nupkg", 1, 3, 3)]
    [InlineData("ConnectorControl-1.3.3-preview.2-win-x64-full.nupkg", 1, 3, 3)]
    [InlineData("ConnectorControl-2.0.10-win-arm64-full.nupkg", 2, 0, 10)]
    public void ParsesFeedVersionFromTheNupkgName(string name, int major, int minor, int patch)
    {
        Assert.True(PackageVerificationCommand.TryParseFeedVersion(name, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Fact]
    public void ReportsOkAndExitsZeroWhenVerifyPasses()
    {
        var nupkg = Nupkg("ConnectorControl-1.0.0-win-x64-full.nupkg");
        var report = dir.File("report.txt");
        var handled = PackageVerificationCommand.TryRun(
            ["--verify-package", nupkg, "--report", report],
            (_, _, _, _) => null,
            RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(0, exitCode);
        // RunningExe (a real assembly on disk, not a Velopack install layout) has no Update.exe
        // two directories up, so FindUpdateExe reports none found.
        Assert.Equal("OK\nchecked: package entries, main executable version (no installed updater found)", File.ReadAllText(report));
    }

    [Fact]
    public void ReportsTheProblemAndExitsOneWhenVerifyFails()
    {
        var nupkg = Nupkg("ConnectorControl-1.0.0-win-x64-full.nupkg");
        var report = dir.File("report.txt");
        var handled = PackageVerificationCommand.TryRun(
            ["--verify-package", nupkg, "--report", report],
            (_, _, _, _) => "not signed by our publisher",
            RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Equal("not signed by our publisher", File.ReadAllText(report));
    }

    [Fact]
    public void VerifyReceivesThePackagePathAndTheParsedVersions()
    {
        var nupkg = Nupkg("ConnectorControl-1.3.3-preview.2-win-x64-full.nupkg");
        var report = dir.File("report.txt");
        string? seenPackage = null;
        Version? seenRunning = null;
        Version? seenFeed = null;
        PackageVerificationCommand.TryRun(
            ["--verify-package", nupkg, "--report", report],
            (package, _, running, feed) =>
            {
                seenPackage = package;
                seenRunning = running;
                seenFeed = feed;
                return null;
            },
            RunningExe, out _);
        Assert.Equal(nupkg, seenPackage);
        Assert.Equal(UpdateVerifier.EmbeddedVersion(RunningExe), seenRunning);
        Assert.Equal(new Version(1, 3, 3), seenFeed);
    }

    [Fact]
    public void AnExceptionInVerificationLandsInTheReport()
    {
        var nupkg = Nupkg("ConnectorControl-1.0.0-win-x64-full.nupkg");
        var report = dir.File("report.txt");
        var handled = PackageVerificationCommand.TryRun(
            ["--verify-package", nupkg, "--report", report],
            (_, _, _, _) => throw new InvalidOperationException("boom"),
            RunningExe, out var exitCode);
        Assert.True(handled);
        Assert.Equal(1, exitCode);
        var reportText = File.ReadAllText(report);
        Assert.Contains(nameof(InvalidOperationException), reportText);
        Assert.Contains("boom", reportText);
    }

    /// <summary>
    /// A Velopack install layout: `&lt;temp&gt;/current/ConnectorControl.exe` with
    /// `&lt;temp&gt;/Update.exe` beside `current/`. The exe is a copy of a real assembly so
    /// <see cref="UpdateVerifier.EmbeddedVersion"/> has something genuine to read.
    /// </summary>
    private string MakeCurrentExe()
    {
        var currentDir = Directory.CreateDirectory(dir.File("current")).FullName;
        var processPath = Path.Combine(currentDir, "ConnectorControl.exe");
        File.Copy(RunningExe, processPath);
        return processPath;
    }

    [Fact]
    public void UpdateExeBesideTheInstallRootIsPassedToTheVerifier()
    {
        var processPath = MakeCurrentExe();
        var updateExePath = dir.File("Update.exe");
        File.WriteAllBytes(updateExePath, []);

        var nupkg = Nupkg("ConnectorControl-1.0.0-win-x64-full.nupkg");
        var report = dir.File("report.txt");
        string? seenUpdateExe = "not set";
        PackageVerificationCommand.TryRun(
            ["--verify-package", nupkg, "--report", report],
            (_, updateExe, _, _) =>
            {
                seenUpdateExe = updateExe;
                return null;
            },
            processPath, out _);
        Assert.Equal(updateExePath, seenUpdateExe);
    }

    [Fact]
    public void AMissingUpdateExeIsPassedAsNull()
    {
        var processPath = MakeCurrentExe();
        // No Update.exe written at dir.File("Update.exe").

        var nupkg = Nupkg("ConnectorControl-1.0.0-win-x64-full.nupkg");
        var report = dir.File("report.txt");
        string? seenUpdateExe = "not set";
        PackageVerificationCommand.TryRun(
            ["--verify-package", nupkg, "--report", report],
            (_, updateExe, _, _) =>
            {
                seenUpdateExe = updateExe;
                return null;
            },
            processPath, out _);
        Assert.Null(seenUpdateExe);
    }
}
