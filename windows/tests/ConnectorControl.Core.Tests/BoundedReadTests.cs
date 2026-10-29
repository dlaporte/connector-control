using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

/// <summary>Mirror: Tests/ConnectorControlCoreTests/BoundedReadTests.swift</summary>
public class BoundedReadTests : IDisposable
{
    private readonly TempDir dir = new("bounded");

    public void Dispose() => dir.Dispose();

    [Fact]
    public void AFileUpToTheLimitIsReadWhole()
    {
        var path = dir.File("doc.json");
        File.WriteAllText(path, "12345");
        Assert.Equal("12345"u8.ToArray(), BoundedRead.Data(path, 5));
    }

    /// <summary>The read itself refuses what is over the limit, with no size checked beforehand to rely on.</summary>
    [Fact]
    public void AFileOverTheLimitIsRefusedByTheReadItself()
    {
        var path = dir.File("doc.json");
        File.WriteAllText(path, "123456");
        var refused = Assert.Throws<BoundedReadException>(() => BoundedRead.Data(path, 5));
        Assert.Equal(BoundedRead.TooLargeError, refused.Message);
    }

    [Fact]
    public void AFolderIsNotAFile()
    {
        var refused = Assert.Throws<BoundedReadException>(() => BoundedRead.Data(dir.Path, 5));
        Assert.Equal(BoundedRead.NotAFileError, refused.Message);
    }

    /// <summary>A file that is not there is reported in the system's own words, as an unbounded read did.</summary>
    [Fact]
    public void AMissingFileSaysWhatAnUnboundedReadSaid()
    {
        var path = dir.File("gone.json");
        var unbounded = Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(path));
        var bounded = Assert.ThrowsAny<IOException>(() => BoundedRead.Data(path, 5));
        Assert.Equal(unbounded.GetType(), bounded.GetType());
        Assert.Equal(unbounded.Message, bounded.Message);
    }
}
