using System.ComponentModel;
using System.Diagnostics;
using ConnectorControl.Core;

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

/// <summary>
/// The Mac's "Show in Finder": a file or folder selected in its parent, or, when it has gone, its
/// parent opened instead (Explorer's <c>/select</c> on a missing path opens an unrelated default
/// location). A failure to launch Explorer is not worth a dialog.
/// </summary>
public static class ExplorerReveal
{
    public static void Show(string path)
    {
        var arguments = File.Exists(path) || Directory.Exists(path)
            ? $"/select,\"{path}\""
            : $"\"{Path.GetDirectoryName(path) ?? path}\"";
        try
        {
            Process.Start(new ProcessStartInfo(SystemPrograms.Explorer, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // nothing more to do
        }
    }
}
