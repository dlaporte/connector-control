using System.Runtime.InteropServices;

namespace ConnectorControl.App.Tests;

/// <summary>
/// Phase 4: the generated app icon (windows/assets/ConnectorControl.ico) is embedded in
/// ConnectorControl.exe, and the asset itself has the frame set Windows wants.
/// </summary>
public class AppIconTests
{
    // With nIconIndex = -1 and no output buffers, ExtractIconEx returns the number of icon
    // resources in the file — 0 for a file without any (unlike Icon.ExtractAssociatedIcon,
    // which silently falls back to the shell's default program icon).
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int iconIndex, nint largeIcons, nint smallIcons, uint count);

    private static string Beside(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    [Fact]
    public void ExecutableCarriesAnIconResource()
    {
        var exe = Beside("ConnectorControl.exe");   // the referenced project's apphost is copied next to the tests
        Assert.True(File.Exists(exe), exe);
        Assert.True(ExtractIconEx(exe, -1, 0, 0, 0) >= 1);
    }

    [Fact]
    public void TestHostHasNoIconResource()
    {
        // Negative control for the P/Invoke: this test host sets no ApplicationIcon.
        var exe = Beside("ConnectorControl.App.Tests.exe");
        Assert.True(File.Exists(exe), exe);
        Assert.Equal(0u, ExtractIconEx(exe, -1, 0, 0, 0));
    }

    [Fact]
    public void IconAssetHasTheSevenFrames()
    {
        var bytes = File.ReadAllBytes(Beside(Path.Combine("Assets", "ConnectorControl.ico")));
        Assert.Equal(0, (int)BitConverter.ToUInt16(bytes, 0));   // reserved
        Assert.Equal(1, (int)BitConverter.ToUInt16(bytes, 2));   // type 1 = icon
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            Assert.Equal(width, height);
            Assert.Equal(32, (int)BitConverter.ToUInt16(bytes, entry + 6));   // bits per pixel
            sizes.Add(width);
        }
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes.ToArray());
    }
}
