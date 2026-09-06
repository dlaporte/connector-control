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
}
