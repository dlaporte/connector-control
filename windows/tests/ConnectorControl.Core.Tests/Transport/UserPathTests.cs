using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Windows only in what it is for; plain text, so it runs on any platform.</summary>
public class UserPathTests
{
    private const string Bin = @"C:\Users\me\AppData\Local\Connector Control\bin";

    [Fact]
    public void TheFolderIsFoundHoweverItIsWritten()
    {
        Assert.True(UserPath.Contains(@"C:\Windows;" + Bin + @";C:\Tools", Bin));
        Assert.True(UserPath.Contains(Bin.ToUpperInvariant() + @"\", Bin), "case and a trailing backslash do not matter");
        Assert.True(UserPath.Contains("\"" + Bin + "\"", Bin), "nor quotes around it");
        Assert.False(UserPath.Contains(@"C:\Windows", Bin));
        Assert.False(UserPath.Contains(null, Bin));
    }

    [Fact]
    public void AddingAppendsOnceAndRemovingTakesOnlyTheFolder()
    {
        Assert.Equal(Bin, UserPath.Adding(null, Bin));
        Assert.Equal(@"C:\Windows;" + Bin, UserPath.Adding(@"C:\Windows", Bin));
        Assert.Equal(@"C:\Windows;" + Bin, UserPath.Adding(@"C:\Windows;", Bin));
        // already there: nothing is added again
        Assert.Equal(@"C:\Windows;" + Bin, UserPath.Adding(@"C:\Windows;" + Bin, Bin));
        // every other entry stays exactly as it was written
        Assert.Equal(@"C:\Windows;;%USERPROFILE%\bin", UserPath.Removing(@"C:\Windows;" + Bin + @";;%USERPROFILE%\bin", Bin));
        Assert.Equal("", UserPath.Removing(Bin, Bin));
    }
}
