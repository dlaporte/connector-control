using System.Windows.Media;
using ConnectorControl.App.Tests.TestSupport;
using ConnectorControl.App.Tray;

namespace ConnectorControl.App.Tests;

public class TrayIconRendererTests
{
    [Theory]
    [InlineData(TrayGlyph.Plug, true, 16)]
    [InlineData(TrayGlyph.Plug, false, 32)]
    [InlineData(TrayGlyph.Warning, true, 24)]
    [InlineData(TrayGlyph.Warning, false, 16)]
    public void RendersVisiblePixelsAtTheRequestedSize(TrayGlyph glyph, bool light, int size)
    {
        var (width, height, visible) = StaRunner.Run(() =>
        {
            var bitmap = TrayIconRenderer.Render(glyph, light, size);
            return (bitmap.PixelWidth, bitmap.PixelHeight, TrayIconRenderer.CountVisiblePixels(bitmap));
        });
        Assert.Equal(size, width);
        Assert.Equal(size, height);
        Assert.True(visible > size * size / 10);   // a glyph, not a blank square
    }

    [Fact]
    public void LightTaskbarGetsABlackGlyphAndDarkTaskbarAWhiteOne()
    {
        var (light, dark) = StaRunner.Run(() => (
            TrayIconRenderer.DominantColor(TrayIconRenderer.Render(TrayGlyph.Plug, lightTaskbar: true, 32)),
            TrayIconRenderer.DominantColor(TrayIconRenderer.Render(TrayGlyph.Plug, lightTaskbar: false, 32))));
        Assert.Equal(Colors.Black, light);
        Assert.Equal(Colors.White, dark);
    }

    [Fact]
    public void SystemIconSizeIsAtLeastSixteenPixels()
    {
        Assert.True(TrayIconRenderer.SystemIconPixelSize() >= 16);
    }
}
