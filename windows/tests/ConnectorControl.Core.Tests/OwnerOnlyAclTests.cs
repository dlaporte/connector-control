using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class OwnerOnlyAclTests : IDisposable
{
    private readonly TempDir dir = new("acl");

    public void Dispose() => dir.Dispose();

    [Fact]
    public void TryApplyOnMissingPathDoesNotThrow()
    {
        OwnerOnlyAcl.TryApply(dir.File("does-not-exist.json"));
    }

    [Fact]
    public void FileBecomesOwnerOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }
        var path = dir.File("f.json");
        File.WriteAllText(path, "{}");
        Assert.False(OwnerOnlyAcl.IsOwnerOnly(path), "a fresh file inherits the temp dir's ACL");
        OwnerOnlyAcl.TryApply(path);
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
        Assert.Equal("{}", File.ReadAllText(path));   // the owner can still read it
    }

    [Fact]
    public void DirectoryBecomesOwnerOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }
        var path = dir.File("sub");
        Directory.CreateDirectory(path);
        OwnerOnlyAcl.TryApply(path);
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
        File.WriteAllText(Path.Combine(path, "child.json"), "{}");   // owner can still create inside
    }

    [Fact]
    public void WriteNewProtectedFileWritesTheBytesOwnerOnly()
    {
        var path = dir.File("created.json");
        Assert.True(OwnerOnlyAcl.WriteNewProtectedFile(path, "{}"u8.ToArray()));
        Assert.Equal("{}", File.ReadAllText(path));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
        }
    }

    [Fact]
    public void WriteNewProtectedFileNeverOverwrites()
    {
        // open(O_CREAT|O_EXCL) semantics on every platform: a name that exists is an error, not a replace.
        var path = dir.File("existing.json");
        File.WriteAllText(path, "old");
        Assert.ThrowsAny<IOException>(() => OwnerOnlyAcl.WriteNewProtectedFile(path, "new"u8.ToArray()));
        Assert.Equal("old", File.ReadAllText(path));
    }

    [Fact]
    public void CreateDirectoryProtectedMakesANewDirectoryOwnerOnlyAndLeavesAnExistingOneAlone()
    {
        var fresh = dir.File(Path.Combine("a", "b"));
        OwnerOnlyAcl.CreateDirectoryProtected(fresh);
        Assert.True(Directory.Exists(fresh));
        var existing = dir.File("existing");
        Directory.CreateDirectory(existing);
        OwnerOnlyAcl.CreateDirectoryProtected(existing);
        Assert.True(Directory.Exists(existing));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(OwnerOnlyAcl.IsOwnerOnly(fresh), "a directory the app creates is private from the start, like the Mac's 0700");
            Assert.False(OwnerOnlyAcl.IsOwnerOnly(existing), "a folder that already existed is not the app's to rewrite");
        }
    }

    [Fact]
    public void CreateDirectoryProtectedThrowsWhenAFileIsInTheWay()
    {
        var blocking = dir.File("blocking");
        File.WriteAllText(blocking, "placeholder");
        Assert.ThrowsAny<IOException>(() => OwnerOnlyAcl.CreateDirectoryProtected(Path.Combine(blocking, "child")));
    }
}
