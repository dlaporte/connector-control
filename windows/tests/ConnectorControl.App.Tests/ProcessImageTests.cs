using ConnectorControl.App.Services;

namespace ConnectorControl.App.Tests;

public class ProcessImageTests
{
    [Fact]
    public void ReadsTheImagePathOfThisProcess()
    {
        var path = ProcessImage.ImagePath(Environment.ProcessId);
        Assert.NotNull(path);
        Assert.True(
            ProcessImage.IsUnder(path, Path.GetDirectoryName(Environment.ProcessPath!)),
            $"expected {path} to sit under {Path.GetDirectoryName(Environment.ProcessPath!)}");
    }

    [Fact]
    public void UnreadableProcessHasNoImagePath()
    {
        // Process 0 is the System Idle Process: OpenProcess always refuses it.
        Assert.Null(ProcessImage.ImagePath(0));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe", @"C:\Program Files\WindowsApps\Claude_1.0.0_x64__pzs8sxrjxfjjc", true)]
    [InlineData(@"C:\PROGRAM FILES\WINDOWSAPPS\Claude_1.0.0\app\claude.exe", @"c:\program files\windowsapps\claude_1.0.0", true)]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\claude.exe", @"C:\Users\me\AppData\Local\AnthropicClaude\", true)]
    // The Claude Code CLI is also claude.exe, but it lives elsewhere.
    [InlineData(@"C:\Users\me\.local\bin\claude.exe", @"C:\Users\me\AppData\Local\AnthropicClaude", false)]
    // A shared prefix is not containment: whole path segments only.
    [InlineData(@"C:\dir\subterranean\claude.exe", @"C:\dir\sub", false)]
    [InlineData(@"C:\dir\claude.exe", @"C:\dir\claude.exe", false)]
    [InlineData(null, @"C:\dir", false)]
    [InlineData(@"C:\dir\claude.exe", null, false)]
    [InlineData(@"C:\dir\claude.exe", "", false)]
    public void IsUnderComparesWholeSegmentsCaseInsensitively(string? imagePath, string? directory, bool expected)
    {
        Assert.Equal(expected, ProcessImage.IsUnder(imagePath, directory));
    }
}
