using System.Runtime.Versioning;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class OwnerOnlyAclTests : IDisposable
{
    private readonly TempDir dir = new("acl");

    public void Dispose() => dir.Dispose();

    [Fact]
    public void TryApplyOnAMissingPathDoesNotThrowAndIsNotApplied()
    {
        // PermissionsSweep counts a path as applied purely on this return value, and its completion
        // rule is "attempted == 0 || applied > 0". A backup deleted out from under the sweep is
        // neither a directory nor a file by the time Apply looks, so it must report false, or
        // vanished entries alone could mark a sweep that secured nothing as done.
        var applied = OwnerOnlyAcl.TryApply(dir.File("does-not-exist.json"));
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.False(applied, "a path with nothing at it must not report itself applied");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void FileBecomesOwnerOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var path = dir.File("f.json");
        File.WriteAllText(path, "{}");
        Assert.False(OwnerOnlyAcl.IsOwnerOnly(path), "a fresh file inherits the temp dir's ACL");
        Assert.True(OwnerOnlyAcl.TryApply(path));
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
        Assert.Equal("{}", File.ReadAllText(path));   // the owner can still read it
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DirectoryBecomesOwnerOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var path = dir.File("sub");
        Directory.CreateDirectory(path);
        Assert.True(OwnerOnlyAcl.TryApply(path));
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
        File.WriteAllText(Path.Combine(path, "child.json"), "{}");   // owner can still create inside
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WriteNewProtectedFileWritesTheBytesOwnerOnly()
    {
        var path = dir.File("created.json");
        Assert.True(OwnerOnlyAcl.WriteNewProtectedFile(path, "{}"u8.ToArray()));
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(path));
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
    [SupportedOSPlatform("windows")]
    public void CreateDirectoryProtectedMakesANewDirectoryOwnerOnlyAndLeavesAnExistingOneAlone()
    {
        var fresh = dir.File(Path.Combine("a", "b"));
        OwnerOnlyAcl.CreateDirectoryProtected(fresh);
        Assert.True(Directory.Exists(fresh));
        var existing = dir.File("existing");
        Directory.CreateDirectory(existing);
        OwnerOnlyAcl.CreateDirectoryProtected(existing);
        Assert.True(Directory.Exists(existing));
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.True(OwnerOnlyAcl.IsOwnerOnly(fresh), "a directory the app creates is private from the start, like the Mac's 0700");
        Assert.False(OwnerOnlyAcl.IsOwnerOnly(existing), "a folder that already existed is not the app's to rewrite");
    }

    [Fact]
    public void CreateDirectoryProtectedThrowsWhenAFileIsInTheWay()
    {
        var blocking = dir.File("blocking");
        File.WriteAllText(blocking, "placeholder");
        Assert.ThrowsAny<IOException>(() => OwnerOnlyAcl.CreateDirectoryProtected(Path.Combine(blocking, "child")));
    }
}
