using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Views;
using ConnectorControl.Core.Services;

namespace ConnectorControl.App.Tests;

public class ClaudeIconLoaderTests
{
    [Fact]
    public void NotFoundInstallHasNoIcon()
    {
        Assert.Null(ClaudeIconLoader.ExecutablePath(ClaudeInstallInfo.NotFound));
        Assert.Null(ClaudeIconLoader.Load(ClaudeInstallInfo.NotFound));
    }

    [Fact]
    public void MissingLegacyExeYieldsNullWithoutThrowing()
    {
        var info = new ClaudeInstallInfo(ClaudeInstallKind.Legacy, null, Path.Combine(Path.GetTempPath(), "cc-missing", "claude.exe"), "claude");
        Assert.Equal(info.LaunchTarget, ClaudeIconLoader.ExecutablePath(info));
        Assert.Null(ClaudeIconLoader.Load(info));
    }

    [Fact]
    public void DesaturateKeepsAlphaAndGreysColor()
    {
        var (color, transparentAlpha) = StaRunner.Run(() =>
        {
            byte[] pixels = [0, 0, 255, 255, 0, 0, 0, 0];   // BGRA: one opaque red pixel, one transparent
            var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var grey = ClaudeIconLoader.Desaturate(source);
            var result = new byte[8];
            grey.CopyPixels(result, 8, 0);
            return (Color.FromArgb(result[3], result[2], result[1], result[0]), result[7]);
        });
        Assert.Equal(255, color.A);
        Assert.Equal(color.R, color.G);
        Assert.Equal(color.G, color.B);
        Assert.Equal(0, transparentAlpha);
    }
}
