using System.Text;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly TempDir dir = new("atomic");

    public void Dispose() => dir.Dispose();

    [Fact]
    public void WriteCreatesFileAndIntermediateDirectories()
    {
        var path = dir.File(Path.Combine("nested", "file.json"));
        AtomicFile.Write(Encoding.UTF8.GetBytes("hello"), path);
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void WriteReplacesExistingFile()
    {
        var path = dir.File("file.json");
        AtomicFile.Write(Encoding.UTF8.GetBytes("one"), path);
        AtomicFile.Write(Encoding.UTF8.GetBytes("two"), path);
        Assert.Equal("two", File.ReadAllText(path));
    }

    [Fact]
    public void WritesArePrivate()
    {
        // testWritesArePrivate: mode 0600 on Mac; an owner-only DACL on Windows.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }
        var path = dir.File("secret.json");
        AtomicFile.Write(Encoding.UTF8.GetBytes("token"), path);
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
    }

    [Fact]
    public void ReplacingAnExistingFileMakesItOwnerOnly()
    {
        // ReplaceFile keeps the replaced file's DACL; Write must still end owner-only.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }
        var path = dir.File("existing.json");
        File.WriteAllText(path, "{}");   // inherits the temp dir's permissive ACL
        Assert.False(OwnerOnlyAcl.IsOwnerOnly(path));
        AtomicFile.Write(Encoding.UTF8.GetBytes("{\"k\":1}"), path);
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
        Assert.Equal("{\"k\":1}", File.ReadAllText(path));
    }

    [Fact]
    public void NoTempFilesLeftBehind()
    {
        AtomicFile.Write(Encoding.UTF8.GetBytes("x"), dir.File("file.json"));
        var names = Directory.GetFiles(dir.Path).Select(p => Path.GetFileName(p)!).ToArray();
        Assert.Equal(["file.json"], names);
    }

    [Fact]
    public void NoTempFilesLeftBehindOnFailure()
    {
        // A FILE where the parent directory should be makes CreateDirectory throw
        // before any temp file exists.
        var blocking = dir.File("blocking");
        File.WriteAllText(blocking, "placeholder");
        var target = dir.File(Path.Combine("blocking", "file.json"));
        Assert.ThrowsAny<IOException>(() => AtomicFile.Write(Encoding.UTF8.GetBytes("test"), target));
        var tmpFiles = Directory.GetFiles(dir.Path).Where(p => p.Contains(".tmp-", StringComparison.Ordinal)).ToArray();
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void WriteReportsWhetherTheFileIsPrivate()
    {
        // Off Windows there is nothing to do; on Windows the temp dir is on an ACL-capable volume.
        var result = AtomicFile.Write(Encoding.UTF8.GetBytes("x"), dir.File("file.json"));
        Assert.True(result.Protected);
    }

    [Fact]
    public void ADirectoryWriteCreatesIsOwnerOnlyWhileAnExistingParentIsUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }
        var path = dir.File(Path.Combine("fresh", "settings.json"));
        AtomicFile.Write(Encoding.UTF8.GetBytes("{}"), path);
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(Path.GetDirectoryName(path)!), "the app's own directory is private from the start, like the Mac's 0700");
        Assert.False(OwnerOnlyAcl.IsOwnerOnly(dir.Path), "a directory that already existed is left as it was");
    }

    /// <summary>A config symlinked into a dotfiles repo is written through: the link survives, the real file gets the bytes.</summary>
    [Fact]
    public void WritesThroughASymlinkedTarget()
    {
        var real = dir.File("real.json");
        File.WriteAllText(real, "{}");
        var link = dir.File("link.json");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating a symlink on Windows needs Developer Mode or elevation; a CI agent
            // without either cannot exercise this, so there is nothing to assert.
            // GitHub's hosted Windows runners are elevated, so under FailSkips=true (ci.runsettings) this skip would surface as a failure if that ever changed.
            Assert.Skip("symlink creation is not permitted in this environment");
            return;
        }
        AtomicFile.Write(Encoding.UTF8.GetBytes("through"), link);
        Assert.NotNull(new FileInfo(link).LinkTarget);   // the link itself survives, not replaced by a plain file
        Assert.Equal("through", File.ReadAllText(real));
        Assert.Equal("through", File.ReadAllText(link));
    }

    [Fact]
    public void SaveStoreReportsTheOutcome()
    {
        var store = new MasterStore([]);
        Assert.True(MasterStoreIO.Save(store, dir.File("mcps.json")).Protected);
    }
}
