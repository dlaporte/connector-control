using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConnectorControl.App.Tests.TestSupport;

/// <summary>Pixel-level assertions over a rendered bitmap; production code never inspects its own pixels.</summary>
internal static class BitmapAssertions
{
    /// <summary>Pixels with any alpha.</summary>
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

    /// <summary>The color of the first fully opaque pixel (Pbgra32 is not premultiplied at alpha 255).</summary>
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
}
