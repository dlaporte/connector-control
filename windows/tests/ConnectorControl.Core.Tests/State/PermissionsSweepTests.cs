using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

public class PermissionsSweepTests : IDisposable
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

        Assert.True(PermissionsSweep.RunOnce(settings, paths));
        Assert.Equal(PermissionsSweep.CurrentVersion, settings.SweepVersion);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(paths.StoreDir));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(paths.MasterStorePath));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(paths.BackupsDir));
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(nestedFile));
        }
        Assert.False(PermissionsSweep.RunOnce(settings, paths));   // gated by the version from now on
    }

    [Fact]
    public void ACompletedSweepRecordsCurrentVersion()
    {
        var paths = new AppPaths(dir.File("claude.json"), dir.File("store"));
        var settings = new FakeSettings();

        Assert.True(PermissionsSweep.RunOnce(settings, paths));
        Assert.Equal(PermissionsSweep.CurrentVersion, settings.SweepVersion);
    }

    [Fact]
    public void AlreadyAtCurrentVersionSkipsTheSweep()
    {
        var paths = new AppPaths(dir.File("claude.json"), dir.File("store"));
        var settings = new FakeSettings { SweepVersion = PermissionsSweep.CurrentVersion };

        Assert.False(PermissionsSweep.RunOnce(settings, paths));
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

        Assert.True(PermissionsSweep.RunOnce(settings, paths));
        Assert.Equal(PermissionsSweep.CurrentVersion, settings.SweepVersion);
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

    /// <summary>
    /// A store directory that IS a shell folder (the user pointed the master list at Documents
    /// itself) is refused outright — not even mcps.json inside it is touched. The refusal is
    /// per directory, by exact match: the app's own backups directory, which the path rule keeps
    /// under LocalAppData rather than under the chosen folder, is still repaired, and so is the
    /// default store location, which sits one level below LocalAppData.
    /// </summary>
    [Fact]
    public void AStoreDirectoryThatIsAShellFolderIsRefusedWhileTheBackupsDirectoryIsStillRepaired()
    {
        var shellFolder = dir.File("Documents");
        var backups = dir.File(Path.Combine("Local", "Connector Control", "backups"));
        var paths = new AppPaths(dir.File("claude.json"), shellFolder, backups);
        Directory.CreateDirectory(shellFolder);
        Directory.CreateDirectory(backups);
        File.WriteAllText(paths.MasterStorePath, "{}");
        var backup = Path.Combine(backups, "mcps.2026-09-10T00-00-00-000Z.json");
        File.WriteAllText(backup, "{}");
        var settings = new FakeSettings { MasterStoreDir = shellFolder };

        Assert.True(PermissionsSweep.RunOnce(settings, paths, [shellFolder]));
        Assert.Equal(PermissionsSweep.CurrentVersion, settings.SweepVersion);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(shellFolder), "a shell folder is never rewritten");
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(paths.MasterStorePath), "nor is anything inside it, the app's own file included");
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(backups), "the app-owned backups directory is still repaired");
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(backup));
        }
    }

    [Fact]
    public void DriveRootsAndListedFoldersAreProtectedByExactMatchOnly()
    {
        var shellFolder = dir.File("Documents");
        Assert.True(PermissionsSweep.IsProtected(Path.GetPathRoot(dir.File("x"))!, []));
        Assert.True(PermissionsSweep.IsProtected(shellFolder, [shellFolder]));
        Assert.True(PermissionsSweep.IsProtected(shellFolder + Path.DirectorySeparatorChar, [shellFolder]), "a trailing separator is the same folder");
        Assert.False(PermissionsSweep.IsProtected(Path.Combine(shellFolder, "Connector Control"), [shellFolder]), "a folder below a protected one is the app's to judge on its own");
        Assert.False(PermissionsSweep.IsProtected(dir.File("elsewhere"), [shellFolder]));
    }

    [Fact]
    public void NothingToAttemptStillMarksTheSweepDone()
    {
        var shellFolder = dir.File("Documents");
        var paths = new AppPaths(dir.File("claude.json"), shellFolder, Path.Combine(shellFolder, "backups"));
        Directory.CreateDirectory(paths.BackupsDir);
        File.WriteAllText(paths.MasterStorePath, "{}");
        var settings = new FakeSettings { MasterStoreDir = shellFolder };

        // Both roots refused: the store directory is the shell folder, the backups directory is listed too.
        Assert.True(PermissionsSweep.RunOnce(settings, paths, [shellFolder, paths.BackupsDir]));
        Assert.Equal(PermissionsSweep.CurrentVersion, settings.SweepVersion);   // nothing was attempted, so there is nothing left to retry
        if (OperatingSystem.IsWindows())
        {
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(shellFolder));
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(paths.MasterStorePath));
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(paths.BackupsDir));
        }
    }

    [Fact]
    public void StoreFileNamesAreTheAppsOwn()
    {
        Assert.True(PermissionsSweep.IsStoreFile("mcps.json"));
        Assert.True(PermissionsSweep.IsStoreFile("mcps.corrupt.2026-09-10T00-00-00-000Z.json"));
        Assert.False(PermissionsSweep.IsStoreFile("mcps.json.bak"));
        Assert.False(PermissionsSweep.IsStoreFile("settings.json"));
        Assert.False(PermissionsSweep.IsStoreFile("notes.txt"));
    }

    [Fact]
    public void MissingDirectoriesAreToleratedAndStillMarkTheSweepDone()
    {
        var paths = new AppPaths(dir.File("claude.json"), dir.File("never-created"));
        var settings = new FakeSettings();
        Assert.True(PermissionsSweep.RunOnce(settings, paths));
        Assert.Equal(PermissionsSweep.CurrentVersion, settings.SweepVersion);
    }
}
