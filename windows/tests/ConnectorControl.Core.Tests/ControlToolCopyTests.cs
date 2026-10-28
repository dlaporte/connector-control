using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests;

/// <summary>Windows only in what it is for — the Mac's tool stays in the bundle — but plain file work, so it
/// runs on any platform, apart from the one fact about a copy that is running.</summary>
public class ControlToolCopyTests : IDisposable
{
    private const string App = @"C:\Users\me\AppData\Local\ConnectorControl\current\ConnectorControl.exe";
    private readonly TempDir dir = new("toolcopy");
    /// <summary>What a refresh said went wrong, as the app writes it to its log.</summary>
    private readonly List<string> failures = [];

    private string Packaged => dir.File(Path.Combine("current", ControlEndpoint.ToolFileName));
    private string DataDir => dir.File("data");
    private string Copy => ControlToolCopy.ToolPath(DataDir);
    private string Bin => ControlToolCopy.BinDir(DataDir);
    private string Record => Path.Combine(Bin, ControlEndpoint.AppPathFileName);

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
        Assert.False(ControlToolCopy.Refresh(Packaged, DataDir, App, failures.Add));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Copy));
        Assert.Empty(Aside());
        Assert.Empty(failures);   // nothing to do is not a failure, and the log hears nothing of it
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
        Assert.False(ControlToolCopy.Refresh(Packaged, DataDir, App, failures.Add));
        Assert.False(Directory.Exists(Bin));
        Assert.Empty(failures);
    }

    /// <summary>A copy that cannot be made — a full disk, an antivirus lock — leaves the old one serving, and
    /// says why. A folder where the new copy is staged fails it the same way on every platform.</summary>
    [Fact]
    public void ACopyThatCannotBeMadeLeavesTheOldOneServingAndSaysWhy()
    {
        Package("v1");
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        Package("v2");
        Directory.CreateDirectory(Copy + ControlToolCopy.StagingSuffix);
        Assert.False(ControlToolCopy.Refresh(Packaged, DataDir, App, failures.Add));
        Assert.Equal("v1", File.ReadAllText(Copy));
        Assert.Empty(Aside());
        Assert.Single(failures);
    }

    /// <summary>The old copy is already renamed aside when the new one cannot take its place: the old one is
    /// moved back, so a copy still serves, and the failure is said.</summary>
    [Fact]
    public void ANewCopyThatCannotTakeItsPlacePutsTheOldOneBack()
    {
        Package("v1");
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        Package("v2");
        var staging = Copy + ControlToolCopy.StagingSuffix;
        void Move(string from, string to)
        {
            if (from == staging)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
            File.Move(from, to);
        }
        Assert.False(ControlToolCopy.Refresh(Packaged, DataDir, App, failures.Add, Move));
        Assert.Equal("v1", File.ReadAllText(Copy));
        Assert.Empty(Aside());
        Assert.Single(failures);
    }

    /// <summary>The record is the app's own step, after the copy: one that cannot be written neither stops the
    /// copy nor goes unseen. A folder where the record goes fails it the same way on every platform.</summary>
    [Fact]
    public void ARecordThatCannotBeWrittenNeitherStopsTheCopyNorGoesUnseen()
    {
        Package("v1");
        Directory.CreateDirectory(Record);
        Assert.True(ControlToolCopy.Refresh(Packaged, DataDir, App, failures.Add));
        Assert.Equal("v1", File.ReadAllText(Copy));
        Assert.Single(failures);
    }

    /// <summary>The record is rewritten only when the app is somewhere else, and then whole: a new record is
    /// written beside it and moved into place, so the tool never reads half of one.</summary>
    [Fact]
    public void TheRecordIsRewrittenOnlyWhenTheAppMoved()
    {
        Package("v1");
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Record, longAgo);
        ControlToolCopy.Refresh(Packaged, DataDir, App);
        Assert.Equal(longAgo, File.GetLastWriteTimeUtc(Record));
        const string moved = @"D:\Apps\ConnectorControl\current\ConnectorControl.exe";
        ControlToolCopy.Refresh(Packaged, DataDir, moved, failures.Add);
        Assert.Equal(moved, File.ReadAllText(Record));
        Assert.False(File.Exists(Record + ControlToolCopy.StagingSuffix));
        Assert.Empty(failures);
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
