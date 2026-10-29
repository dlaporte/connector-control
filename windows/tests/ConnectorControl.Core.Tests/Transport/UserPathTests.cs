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
        // a trailing empty entry stays as written, so removing gives back the PATH as it was
        Assert.Equal(@"C:\Windows;;" + Bin, UserPath.Adding(@"C:\Windows;", Bin));
        Assert.Equal(@"C:\Windows;", UserPath.Removing(UserPath.Adding(@"C:\Windows;", Bin), Bin));
        Assert.Equal(@"C:\Windows;;;" + Bin, UserPath.Adding(@"C:\Windows;;", Bin));
        // already there: nothing is added again
        Assert.Equal(@"C:\Windows;" + Bin, UserPath.Adding(@"C:\Windows;" + Bin, Bin));
        // every other entry stays exactly as it was written
        Assert.Equal(@"C:\Windows;;%USERPROFILE%\bin", UserPath.Removing(@"C:\Windows;" + Bin + @";;%USERPROFILE%\bin", Bin));
        Assert.Equal("", UserPath.Removing(Bin, Bin));
    }

    [Fact]
    public void ForwardSlashesAndEveryDuplicateOrVariableSpellingCount()
    {
        Assert.True(UserPath.Contains(@"C:/Users/me/AppData/Local/Connector Control/bin", Bin));
        Environment.SetEnvironmentVariable("CC_TEST_LOCAL", @"C:\Users\me\AppData\Local");
        try
        {
            var path = @"C:\Windows;" + Bin + @";%CC_TEST_LOCAL%\Connector Control\bin\;" + Bin.ToUpperInvariant() + @";C:\Tools";
            Assert.Equal(@"C:\Windows;C:\Tools", UserPath.Removing(path, Bin));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_TEST_LOCAL", null);
        }
    }

    /// <summary>The registry's Path value is read only as text. A list or binary data there is no PATH this app can
    /// change without losing it, so reading it fails, and nothing is written over it.</summary>
    [Fact]
    public void TheRegistryValueIsReadOnlyAsText()
    {
        Assert.Null(UserPath.Text(null));
        Assert.Equal(@"C:\Windows;%USERPROFILE%\bin", UserPath.Text(@"C:\Windows;%USERPROFILE%\bin"));
        Assert.Equal(UserPath.NotTextError, Assert.Throws<IOException>(() => UserPath.Text(new[] { @"C:\Windows" })).Message);
        Assert.Equal(UserPath.NotTextError, Assert.Throws<IOException>(() => UserPath.Text(new byte[] { 1, 2 })).Message);
        Assert.Equal(UserPath.NotTextError, Assert.Throws<IOException>(() => UserPath.Text(7)).Message);
    }
}
