using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ConnectorControl.Core.State;
using Microsoft.Win32;

namespace ConnectorControl.App.Views;

/// <summary>
/// Publishing binds the collection to a folder it rewrites on every change; exporting writes the
/// same document once and binds nothing. The sheet is the same sheet because the decision the
/// author is making — what travels — is the same one.
/// </summary>
public enum PublishDialogMode
{
    Publish,
    Export,
}

/// <summary>
/// The Publish sheet and, with one flag flipped, the Export sheet: layout, bindings, the two
/// native pickers, and the refresh a row edit needs. Every rule and string is PublishModel's.
/// </summary>
public partial class PublishDialog : DialogWindow
{
    private readonly PropertyChangedEventHandler onModelChanged;

    public PublishDialog(PublishModel model, PublishDialogMode mode)
    {
        InitializeComponent();
        Model = model;
        Mode = mode;
        DataContext = model;
        Title = mode == PublishDialogMode.Publish
            ? model.SheetTitle
            // Exporting borrows the menu item's own wording, which names the collection it writes.
            : FlyoutModel.ExportTitleFor(model.Collection);
        TitleText.Text = Title;
        FolderRow.Visibility = mode == PublishDialogMode.Publish ? Visibility.Visible : Visibility.Collapsed;
        PublishButton.Visibility = mode == PublishDialogMode.Publish ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = mode == PublishDialogMode.Export ? Visibility.Visible : Visibility.Collapsed;
        PublishButton.IsDefault = mode == PublishDialogMode.Publish;
        ExportButton.IsDefault = mode == PublishDialogMode.Export;
        // The row lists are fixed for the life of the sheet, so a section with nothing in it is
        // decided once here rather than bound to a count.
        EnvSection.Visibility = model.EnvRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PathsSection.Visibility = model.PathRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        onModelChanged = (_, _) => Refresh();
        Model.PropertyChanged += onModelChanged;
        Closed += (_, _) => Model.PropertyChanged -= onModelChanged;
    }

    public PublishModel Model { get; }

    public PublishDialogMode Mode { get; }

    /// <summary>
    /// Whether the sheet published or exported before it closed. A plain property, like every other
    /// dialog here: Window.DialogResult's setter throws on a window presented with Show(), which is
    /// how the tests present this one.
    /// </summary>
    public bool Accepted { get; private set; }

    public static bool Show(Window? owner, PublishModel model, PublishDialogMode mode)
    {
        var dialog = new PublishDialog(model, mode);
        return Present(dialog, owner, () => dialog.Accepted);
    }

    /// <summary>
    /// What the preview, the warnings and the Publish button say is derived from the rows and the
    /// folder, and a row is a plain object that notifies nobody when a tick or a hint changes —
    /// the Mac's SwiftUI re-reads the whole sheet for free, and here the sheet asks each of those
    /// bindings to re-read. The document is a few kilobytes; rendering it per edit is what the Mac
    /// does per keystroke too.
    /// </summary>
    private void Refresh()
    {
        PreviewBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        WarningList.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
        PublishButton.GetBindingExpression(IsEnabledProperty)?.UpdateTarget();
        FooterText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
    }

    private void OnRowTicked(object sender, RoutedEventArgs e) => Refresh();

    private void OnRowTyped(object sender, TextChangedEventArgs e)
    {
        // TextChanged and the binding's own source update both answer the same keystroke; pushing
        // the value first is what makes the preview below agree with the field above it.
        ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Refresh();
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose", Multiselect = false };
        if (picker.ShowDialog(this) == true)
        {
            Model.Folder = picker.FolderName;
            ShowFailure(null);
        }
    }

    private void OnPublish(object sender, RoutedEventArgs e) => Finish(Model.Publish());

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { FileName = Model.FileName, Filter = "Collection (*.json)|*.json" };
        if (picker.ShowDialog(this) == true)
        {
            Finish(Model.Export(picker.FileName));
        }
    }

    /// <summary>
    /// The model answers with the reason it could not write, or null. A failure stays on the sheet
    /// the user is looking at; a success closes it as accepted.
    /// </summary>
    private void Finish(string? failure)
    {
        ShowFailure(failure);
        if (failure is null)
        {
            Accepted = true;
            Close();
        }
    }

    private void ShowFailure(string? failure)
    {
        FailureText.Text = failure ?? string.Empty;
        FailureText.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
