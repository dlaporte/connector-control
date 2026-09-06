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
        var (width, height, visible, iconHandle, iconWidth) = StaRunner.Run(() =>
        {
            var bitmap = TrayIconRenderer.Render(glyph, light, size);
            // The tray takes a System.Drawing.Icon and RenderIcon is the only conversion that
            // works, so it is exercised here at every size: assigning the bitmap to
            // TaskbarIcon.IconSource instead threw NotImplementedException at startup.
            using var icon = TrayIconRenderer.RenderIcon(glyph, light, size);
            return (bitmap.PixelWidth, bitmap.PixelHeight, TrayIconRenderer.CountVisiblePixels(bitmap), icon.Handle, icon.Width);
        });
        Assert.Equal(size, width);
        Assert.Equal(size, height);
        Assert.True(visible > size * size / 10);   // a glyph, not a blank square
        Assert.True(iconHandle != nint.Zero, "RenderIcon produced a usable tray icon");
        // Icon.Width is the ICONDIRENTRY's bWidth, so this proves IconBytes wrote the requested
        // size into the directory: a wrong entry size still yields a usable handle, but the tray
        // would then scale the glyph.
        Assert.Equal(size, iconWidth);
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
