using System.Runtime.InteropServices;
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
    /// <summary>A plug seen from the front (two prongs up, body, cable stub) in a 24×24 box.</summary>
    public const string PlugPathData = "F1 M7,1 h2 v6 h-2 z M15,1 h2 v6 h-2 z M4,7 h16 v5 a5,5 0 0 1 -5,5 h-6 a5,5 0 0 1 -5,-5 z M10.5,17 h3 v6 h-3 z";

    /// <summary>A filled triangle with an even-odd exclamation cut-out, 24×24 box.</summary>
    public const string WarningPathData = "F0 M12,2 L22.5,21 H1.5 Z M10.9,8 h2.2 v6.5 h-2.2 z M10.9,16.2 h2.2 v2.3 h-2.2 z";

    private const double DesignSize = 24.0;
    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;

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

    /// <summary>
    /// The same glyph as a <see cref="System.Drawing.Icon"/>, which is what the tray shows.
    /// H.NotifyIcon's <c>TaskbarIcon.IconSource</c> cannot carry this: its ImageSource-to-icon
    /// conversion only resolves a <c>BitmapImage</c>'s <c>UriSource</c> or a <c>BitmapFrame</c>'s
    /// URI and throws <c>NotImplementedException</c> for anything rendered at runtime, so the
    /// icon goes through <c>TaskbarIcon.Icon</c> instead (which disposes the one it replaces).
    /// </summary>
    public static System.Drawing.Icon RenderIcon(TrayGlyph glyph, bool lightTaskbar, int pixelSize)
    {
        using var ico = new MemoryStream(IconBytes(Render(glyph, lightTaskbar, pixelSize)));
        return new System.Drawing.Icon(ico, pixelSize, pixelSize);
    }

    /// <summary>
    /// A one-image .ico wrapping a PNG frame — the PNG-compressed icon entry Windows has
    /// accepted since Vista, and the only .ico shape needed for a single-size tray glyph.
    /// Building the bytes (rather than handing over a raw HICON) keeps the Icon the owner of
    /// its own handle, so disposing it really frees it.
    /// </summary>
    internal static byte[] IconBytes(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        var image = png.ToArray();

        using var ico = new MemoryStream();
        var writer = new BinaryWriter(ico);
        writer.Write((short)0);                                                     // ICONDIR.idReserved
        writer.Write((short)1);                                                     // idType: 1 = icon
        writer.Write((short)1);                                                     // idCount: one image
        writer.Write((byte)(bitmap.PixelWidth >= 256 ? 0 : bitmap.PixelWidth));     // 0 means 256
        writer.Write((byte)(bitmap.PixelHeight >= 256 ? 0 : bitmap.PixelHeight));
        writer.Write((byte)0);                                                      // palette entries: none
        writer.Write((byte)0);                                                      // bReserved
        writer.Write((short)1);                                                     // wPlanes
        writer.Write((short)32);                                                    // wBitCount: BGRA
        writer.Write(image.Length);                                                 // dwBytesInRes
        writer.Write(IconDirSize + IconDirEntrySize);                               // dwImageOffset
        writer.Write(image);
        writer.Flush();
        return ico.ToArray();
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
