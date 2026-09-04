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

    [Fact]
    public void MissingDirectoriesAreToleratedAndStillMarkTheSweepDone()
    {
        var paths = new AppPaths(dir.File("claude.json"), dir.File("never-created"));
        var settings = new FakeSettings();
        Assert.True(AclSweep.RunOnce(settings, paths));
        Assert.True(settings.AclSweepDone);
    }
}
