using System.ComponentModel;
using System.Diagnostics;

namespace ConnectorControl.App.Views;

/// <summary>Opens an http(s) URL in the default browser. A browser that fails to launch is not worth a dialog.</summary>
public static class ExternalLink
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            // nothing more to do
        }
    }
}
