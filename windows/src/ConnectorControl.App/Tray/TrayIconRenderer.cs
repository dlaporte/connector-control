using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConnectorControl.App.Tray;

/// <summary>
/// Renders the two tray glyphs from vector data at runtime, black for a light
/// taskbar and white for a dark one, at the taskbar's native icon size. No ICO
/// assets: one geometry serves every DPI and both themes.
/// </summary>
public static class TrayIconRenderer
{
    /// <summary>
    /// Forces software rendering before the first Visual is ever created.
    /// A GPU-less host (a CI runner, a headless VM, an RDP session with no
    /// hardware acceleration) makes WPF's automatic hardware-tier probe on
    /// first render unreliable; observed on Windows CI as the whole test
    /// process being killed with STATUS_STACK_OVERFLOW (0xC00000FD) on the
    /// very first DrawingVisual render. Skipping that probe avoids it.
    /// </summary>
    static TrayIconRenderer()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    /// <summary>A plug seen from the front (two prongs up, body, cable stub) in a 24×24 box.</summary>
    public const string PlugPathData = "F1 M7,1 h2 v6 h-2 z M15,1 h2 v6 h-2 z M4,7 h16 v5 a5,5 0 0 1 -5,5 h-6 a5,5 0 0 1 -5,-5 z M10.5,17 h3 v6 h-3 z";

    /// <summary>A filled triangle with an even-odd exclamation cut-out, 24×24 box.</summary>
    public const string WarningPathData = "F0 M12,2 L22.5,21 H1.5 Z M10.9,8 h2.2 v6.5 h-2.2 z M10.9,16.2 h2.2 v2.3 h-2.2 z";

    private const double DesignSize = 24.0;

    public static BitmapSource Render(TrayGlyph glyph, bool lightTaskbar, int pixelSize)
    {
        var geometry = Geometry.Parse(glyph == TrayGlyph.Plug ? PlugPathData : WarningPathData);
        var brush = lightTaskbar ? Brushes.Black : Brushes.White;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var scale = pixelSize / DesignSize;
            context.PushTransform(new ScaleTransform(scale, scale));
            context.DrawGeometry(brush, null, geometry);
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>16 px at 100 % scaling, 24 at 150 %, 32 at 200 %.</summary>
    public static int SystemIconPixelSize()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return Math.Max(16, (int)Math.Round(16 * dpi / 96.0));
        }
        catch (EntryPointNotFoundException)
        {
            return 16;   // pre-1607 Windows 10: not a supported OS, but never crash over an icon size
        }
    }

    /// <summary>Test helper: pixels with any alpha.</summary>
    public static int CountVisiblePixels(BitmapSource bitmap)
    {
        var pixels = Pixels(bitmap);
        var count = 0;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 0)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Test helper: the color of the first fully opaque pixel (Pbgra32 is not premultiplied at alpha 255).</summary>
    public static Color DominantColor(BitmapSource bitmap)
    {
        var pixels = Pixels(bitmap);
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 255)
            {
                return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
            }
        }
        return Colors.Transparent;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = bitmap.Format == PixelFormats.Pbgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
