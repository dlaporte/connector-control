using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests;

/// <summary>Windows only in what it is for — the Mac's tool stays in the bundle — but plain file work, so it
/// runs on any platform, apart from the one fact about a copy that is running.</summary>
public class ControlToolCopyTests : IDisposable
{
    private const string App = @"C:\Users\me\AppData\Local\ConnectorControl\current\ConnectorControl.exe";
    private readonly TempDir dir = new("toolcopy");

    private string Packaged => dir.File(Path.Combine("current", ControlEndpoint.ToolFileName));
    private string DataDir => dir.File("data");
    private string Copy => ControlToolCopy.ToolPath(DataDir);
    private string Bin => ControlToolCopy.BinDir(DataDir);

    public void Dispose() => dir.Dispose();

    private void Package(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Packaged)!);
        File.WriteAllText(Packaged, content);
    }

    private string[] Aside() => Directory.EnumerateFiles(Bin, ControlToolCopy.AsidePrefix + "*").ToArray();

    [Fact]
    public void TheToolIsCopiedOutsideTheUpdateFolderAndTheAppIsRecorded()
    {
        Package("v1");
        Assert.True(ControlToolCopy.Refresh(Packaged, DataDir, App));
        Assert.Equal(Path.Combine(DataDir, "bin", "connector-control.exe"), Copy);
        Assert.Equal("v1", File.ReadAllText(Copy));
        Assert.Equal(App, File.ReadAllText(Path.Combine(Bin, ControlEndpoint.AppPathFileName)));
    }

    [Fact]
    public void AnUnchangedToolIsLeftAlone()
    {
        Package("v1");
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        var written = File.GetLastWriteTimeUtc(Copy);
        Assert.False(ControlToolCopy.Refresh(Packaged, DataDir, App));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Copy));
        Assert.Empty(Aside());
    }

    [Fact]
    public void AChangedToolReplacesTheCopyAndTheOldOneGoesAtTheNextLaunch()
    {
        Package("v1");
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        Package("v2");
        Assert.True(ControlToolCopy.Refresh(Packaged, DataDir, App));
        Assert.Equal("v2", File.ReadAllText(Copy));
        Assert.Equal(new[] { "v1" }, Aside().Select(File.ReadAllText));
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        Assert.Empty(Aside());
        Assert.False(File.Exists(Copy + ControlToolCopy.StagingSuffix));
    }

    /// <summary>A running exe is open with read and delete sharing, as the loader opens it: it cannot be
    /// overwritten, but it can be renamed, so the new copy takes its place while it runs on.</summary>
    [Fact]
    public void ACopyThatIsRunningIsRenamedAsideNotOverwritten()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Package("v1");
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        using (new FileStream(Copy, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(Copy, "overwritten"));
            Package("v2");
            Assert.True(ControlToolCopy.Refresh(Packaged, DataDir, App));
        }
        Assert.Equal("v2", File.ReadAllText(Copy));
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        Assert.Empty(Aside());
    }

    /// <summary>A development run has no packaged tool beside it: it touches neither the copy nor the record, so
    /// the installed relay never starts a development build against the live store.</summary>
    [Fact]
    public void WithNoPackagedToolNothingIsWritten()
    {
        Assert.False(ControlToolCopy.Refresh(Packaged, DataDir, App));
        Assert.False(Directory.Exists(Bin));
    }

    [Fact]
    public void RemovingTakesTheFolderAway()
    {
        Package("v1");
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        ControlToolCopy.Remove(DataDir);
        Assert.False(Directory.Exists(Bin));
        ControlToolCopy.Remove(DataDir);   // nothing there is fine
    }
}
