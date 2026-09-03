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
}
