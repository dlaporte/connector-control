using System.Windows;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>Spec §6.7: the new version, the release notes rendered from Markdown, Install and Relaunch / Later.</summary>
public partial class UpdateDialog : DialogWindow
{
    public UpdateDialog(string newVersion, string currentVersion, string? notesMarkdown)
    {
        InitializeComponent();
        Headline.Text = UpdateCoordinator.AvailableHeadline;
        Detail.Text = UpdateCoordinator.AvailableDetail(newVersion, currentVersion);
        Notes.Document = Markdig.Wpf.Markdown.ToFlowDocument(notesMarkdown ?? string.Empty, null);
        InstallButton.Content = UpdateCoordinator.InstallButton;
        LaterButton.Content = UpdateCoordinator.LaterButton;
    }

    /// <summary>True for Install and Relaunch.</summary>
    public bool Result { get; private set; }

    public static bool Show(Window? owner, string newVersion, string currentVersion, string? notesMarkdown)
    {
        var dialog = new UpdateDialog(newVersion, currentVersion, notesMarkdown);
        return Present(dialog, owner, () => dialog.Result);
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();
}
