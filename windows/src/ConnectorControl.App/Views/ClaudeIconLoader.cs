using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConnectorControl.Core.Services;
using Windows.Management.Deployment;

namespace ConnectorControl.App.Views;

/// <summary>
/// Spec §7.3: the Claude tab icon is Claude's own icon, extracted from the
/// resolved exe and desaturated (the Mac uses the template tray glyph). Best
/// effort — null means "use the generic glyph".
/// </summary>
public static class ClaudeIconLoader
{
    public static ImageSource? Load(ClaudeInstallInfo info)
    {
        var exe = ExecutablePath(info);
        if (exe is null || !File.Exists(exe))
        {
            return null;
        }
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null)
            {
                return null;
            }
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
            return Desaturate(source);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or COMException or Win32Exception)
        {
            return null;   // WindowsApps folders are often unreadable; the generic glyph is fine
        }
    }

    internal static string? ExecutablePath(ClaudeInstallInfo info) => info.Kind switch
    {
        ClaudeInstallKind.Legacy => info.LaunchTarget,
        ClaudeInstallKind.Msix => MsixExecutable(info.PackageFamilyName),
        _ => null,
    };

    /// <summary>Luma greyscale that keeps the alpha channel (a Gray8 conversion would paint transparent pixels black).</summary>
    internal static BitmapSource Desaturate(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            var luma = (byte)((pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000);
            pixels[i] = luma;
            pixels[i + 1] = luma;
            pixels[i + 2] = luma;
        }
        var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static string? MsixExecutable(string? family)
    {
        if (family is null)
        {
            return null;
        }
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                if (package.Id.FamilyName != family)
                {
                    continue;
                }
                var root = package.InstalledLocation.Path;
                var candidate = Path.Combine(root, "app", "claude.exe");
                return File.Exists(candidate) ? candidate : Directory.EnumerateFiles(root, "claude.exe", SearchOption.AllDirectories).FirstOrDefault();
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidOperationException
            or FileNotFoundException or TypeLoadException or PlatformNotSupportedException)
        {
            // WinRT unavailable or the package folder is unreadable
        }
        return null;
    }
}
