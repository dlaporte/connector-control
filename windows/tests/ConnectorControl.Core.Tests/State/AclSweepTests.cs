using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class AclSweepTests : IDisposable
{
    private readonly TempDir dir = new("acl");

    public void Dispose() => dir.Dispose();

    [Fact]
    public void SweepsEveryFileAndDirectoryOnceAndSetsTheFlag()
    {
        var paths = new AppPaths(dir.File("claude.json"), dir.File("store"));
        Directory.CreateDirectory(Path.Combine(paths.BackupsDir, "nested"));
        File.WriteAllText(paths.MasterStorePath, "{}");
        var nestedFile = Path.Combine(paths.BackupsDir, "nested", "b.json");
        File.WriteAllText(nestedFile, "{}");
        var settings = new FakeSettings();

        Assert.True(AclSweep.RunOnce(settings, paths));
        Assert.True(settings.AclSweepDone);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(paths.StoreDir));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(paths.MasterStorePath));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(paths.BackupsDir));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(nestedFile));
        }
        Assert.False(AclSweep.RunOnce(settings, paths));   // gated by the flag from now on
    }

    /// <summary>
    /// The store directory can be a folder the user chose (OneDrive, Documents, a repo): only the
    /// app's own files there are touched, nothing else in it or below it, and its own DACL is left
    /// alone. The backups directory is always the app's and is swept in full.
    /// </summary>
    [Fact]
    public void AChosenStoreDirectoryKeepsItsOwnFilesAndAcl()
    {
        var chosen = dir.File(Path.Combine("OneDrive", "connectors"));
        var backups = dir.File(Path.Combine("Local", "Connector Control", "backups"));
        var paths = new AppPaths(dir.File("claude.json"), chosen, backups);
        var strangerDir = Path.Combine(chosen, "photos");
        Directory.CreateDirectory(strangerDir);
        Directory.CreateDirectory(backups);
        var strangerFile = Path.Combine(chosen, "notes.txt");
        var nestedStranger = Path.Combine(strangerDir, "a.jpg");
        var corrupt = Path.Combine(chosen, "mcps.corrupt.2026-09-10T00-00-00-000Z.json");
        var backup = Path.Combine(backups, "mcps.2026-09-10T00-00-00-000Z.json");
        foreach (var file in new[] { paths.MasterStorePath, strangerFile, nestedStranger, corrupt, backup })
        {
            File.WriteAllText(file, "{}");
        }
        var settings = new FakeSettings { MasterStoreDir = chosen };

        Assert.True(AclSweep.RunOnce(settings, paths));
        Assert.True(settings.AclSweepDone);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(chosen), "a chosen folder's own DACL is not the app's to rewrite");
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(strangerFile), "other files in the chosen folder are untouched");
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(strangerDir), "nothing below the chosen folder is touched");
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(nestedStranger));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(paths.MasterStorePath));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(corrupt));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(backups));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(backup));
        }
    }

    [Fact]
    public void ADriveRootOrShellFolderIsNeverSwept()
    {
        var shellFolder = dir.File("Documents");
        var paths = new AppPaths(dir.File("claude.json"), shellFolder, Path.Combine(shellFolder, "backups"));
        Directory.CreateDirectory(paths.BackupsDir);
        File.WriteAllText(paths.MasterStorePath, "{}");
        File.WriteAllText(Path.Combine(paths.BackupsDir, "mcps.2026-09-10T00-00-00-000Z.json"), "{}");
        var settings = new FakeSettings();

        Assert.True(AclSweep.RunOnce(settings, paths, [shellFolder]));
        Assert.True(settings.AclSweepDone, "nothing was attempted, so there is nothing left to retry");
        if (OperatingSystem.IsWindows())
        {
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(shellFolder));
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(paths.MasterStorePath));
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(paths.BackupsDir));
        }
        Assert.True(AclSweep.IsProtected(Path.GetPathRoot(dir.File("x"))!, []));
        Assert.True(AclSweep.IsProtected(shellFolder + Path.DirectorySeparatorChar, [shellFolder]));
        Assert.False(AclSweep.IsProtected(paths.BackupsDir, [shellFolder]));
    }

    [Fact]
    public void StoreFileNamesAreTheAppsOwn()
    {
        Assert.True(AclSweep.IsStoreFile("mcps.json"));
        Assert.True(AclSweep.IsStoreFile("mcps.corrupt.2026-09-10T00-00-00-000Z.json"));
        Assert.False(AclSweep.IsStoreFile("mcps.json.bak"));
        Assert.False(AclSweep.IsStoreFile("settings.json"));
        Assert.False(AclSweep.IsStoreFile("notes.txt"));
    }

    [Fact]
    public void MissingDirectoriesAreToleratedAndStillMarkTheSweepDone()
    {
        var paths = new AppPaths(dir.File("claude.json"), dir.File("never-created"));
        var settings = new FakeSettings();
        Assert.True(AclSweep.RunOnce(settings, paths));
        Assert.True(settings.AclSweepDone);
    }
}
