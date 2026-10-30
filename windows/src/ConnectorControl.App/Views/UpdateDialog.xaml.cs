using System.Windows;
using System.Windows.Documents;
using ConnectorControl.Core.State;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Wpf.Inlines;
using Markdig.Syntax.Inlines;

namespace ConnectorControl.App.Views;

/// <summary>
/// The new version, the release notes rendered from Markdown, Install and Relaunch / Later. Install and Relaunch
/// keeps the dialog up, busy, reading Downloading…, until the app relaunches or the download fails.
/// </summary>
public partial class UpdateDialog : DialogWindow
{
    private readonly Func<Task> install;
    private bool busy;

    public UpdateDialog(string newVersion, string currentVersion, string? notesMarkdown, Func<Task> install)
    {
        InitializeComponent();
        this.install = install;
        // While the package downloads the dialog cannot be closed: closed, it would answer Later while the
        // download ran on and the app then relaunched anyway.
        Closing += (_, e) => e.Cancel = busy;
        Headline.Text = UpdateCoordinator.AvailableHeadline;
        Detail.Text = UpdateCoordinator.AvailableDetail(newVersion, currentVersion);
        Notes.Document = RenderNotes(notesMarkdown ?? string.Empty);
        InstallButton.Content = UpdateCoordinator.InstallButton;
        LaterButton.Content = UpdateCoordinator.LaterButton;
    }

    /// <summary>
    /// The release notes as Markdig.Wpf renders them, except that an image is shown as its alt text and never
    /// loaded. The notes come from the release feed, which nothing signs, and the dialog opens before the package
    /// is checked: Markdig.Wpf would build a BitmapImage for every image as it renders, fetching an http URL (a
    /// beacon) or opening a file:// share (handing Windows' sign-in to whoever runs it). Links stay links; a
    /// click is the person's own choice. Windows only: the Mac's notes come inside the signed Sparkle feed.
    /// </summary>
    internal static FlowDocument RenderNotes(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder().Build();
        var document = new FlowDocument();
        var renderer = new WpfRenderer(document);
        if (!renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new LinkWithoutImageRenderer()))
        {
            // Markdig.Wpf no longer renders links with that class: rendering on would load images again.
            throw new InvalidOperationException("Markdig.Wpf's link renderer was not found, so the release notes cannot be shown safely.");
        }
        pipeline.Setup(renderer);
        renderer.Render(Markdown.Parse(markdown, pipeline));
        return document;
    }

    /// <summary>Markdig.Wpf's link renderer for links; an image is written as the text of its alt, which may hold another image, rendered the same way.</summary>
    private sealed class LinkWithoutImageRenderer : LinkInlineRenderer
    {
        protected override void Write(WpfRenderer renderer, LinkInline link)
        {
            if (link.IsImage)
            {
                renderer.WriteChildren(link);
                return;
            }
            base.Write(renderer, link);
        }
    }

    /// <summary>True for Install and Relaunch.</summary>
    public bool Result { get; private set; }

    public static bool Show(Window? owner, string newVersion, string currentVersion, string? notesMarkdown, Func<Task> install)
    {
        var dialog = new UpdateDialog(newVersion, currentVersion, notesMarkdown, install);
        return Present(dialog, owner, () => dialog.Result);
    }

    /// <summary>Busy while <c>install</c> runs: both buttons off, Install and Relaunch reading Downloading…. The modal loop
    /// keeps pumping meanwhile, which is where the relaunch is posted. <c>install</c> never throws (IDialogs.OfferUpdate).</summary>
    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        Result = true;
        SetBusy(true);
        try
        {
            await install();
        }
        finally
        {
            SetBusy(false);
            Close();
        }
    }

    internal void SetBusy(bool on)
    {
        busy = on;
        InstallButton.IsEnabled = !on;
        LaterButton.IsEnabled = !on;
        InstallButton.Content = on ? UpdateCoordinator.DownloadingTitle : UpdateCoordinator.InstallButton;
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();
}
