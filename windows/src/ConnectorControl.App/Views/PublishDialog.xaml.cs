using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ConnectorControl.Core.State;
using Microsoft.Win32;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Publish dialog and, in the model's other mode, the Export dialog: layout, bindings and the
/// two native pickers. Every rule and string is PublishModel's — the mode included, and with it
/// the title, the gate and the verb — and so is every refresh: a tick or a keystroke reaches the
/// preview because the row it changed raises it, not because this window nudges a binding.
/// </summary>
public partial class PublishDialog : DialogWindow
{
    public PublishDialog(PublishModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;
        Title = model.SheetTitle;
        TitleText.Text = Title;
        // Export asks the save dialog for a path when its button is pressed, so it has no folder
        // row. One button per mode, each the default of its own dialog.
        var publishing = model.SheetMode == PublishModel.Mode.Publish;
        FolderRow.Visibility = publishing ? Visibility.Visible : Visibility.Collapsed;
        PublishButton.Visibility = publishing ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = publishing ? Visibility.Collapsed : Visibility.Visible;
        PublishButton.IsDefault = publishing;
        ExportButton.IsDefault = !publishing;
    }

    public PublishModel Model { get; }

    /// <summary>
    /// Whether the sheet published or exported before it closed. A plain property, like every other
    /// dialog here: Window.DialogResult's setter throws on a window presented with Show(), which is
    /// how the tests present this one.
    /// </summary>
    public bool Accepted { get; private set; }

    public static bool Show(Window? owner, PublishModel model)
    {
        var dialog = new PublishDialog(model);
        return Present(dialog, owner, () => dialog.Accepted);
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose", Multiselect = false };
        if (picker.ShowDialog(this) == true)
        {
            Model.Folder = picker.FolderName;
            ShowFailure(FailureText, null);
        }
    }

    /// <summary>Each unresolved mark's line is bound to that mark, whose id is all the model needs.</summary>
    private void OnForgetMark(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PublishModel.UnresolvedMark mark)
        {
            Model.ForgetUnresolvedMark(mark.Id);
        }
    }

    /// <summary>
    /// Each kept path's line is bound to that path, whose value is all the model needs. A refused
    /// release answers with the entry's note, which the failure line then says; one that goes
    /// through clears whatever an earlier answer left there.
    /// </summary>
    private void OnReleaseValue(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PublishModel.KeptPath kept)
        {
            ShowFailure(FailureText, Model.ReleaseKeptPath(kept.Value));
        }
    }

    /// <summary>
    /// A folder entry's line is bound to that entry, which the model rewrites where it sits. The
    /// rewrite is a save of the connector, so it can fail, and its message goes where a failed
    /// publish's does.
    /// </summary>
    private void OnUseDirectoryToken(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PublishModel.KeptPath kept)
        {
            ShowFailure(FailureText, Model.UseDirectoryToken(kept));
        }
    }

    /// <summary>
    /// The model's verb. An export first asks the save dialog where to write, and a dialog
    /// cancelled writes nothing.
    /// </summary>
    private void OnFinish(object sender, RoutedEventArgs e)
    {
        string? path = null;
        if (Model.SheetMode == PublishModel.Mode.Export)
        {
            var picker = new SaveFileDialog { FileName = Model.FileName, Filter = "Collection (*.json)|*.json" };
            if (picker.ShowDialog(this) != true)
            {
                return;
            }
            path = picker.FileName;
        }
        Finish(Model.Finish(path));
    }

    /// <summary>
    /// The model answers with the reason it could not write, or null. A failure stays on the sheet
    /// the user is looking at; a success closes it as accepted.
    /// </summary>
    private void Finish(string? failure)
    {
        ShowFailure(FailureText, failure);
        if (failure is null)
        {
            Accepted = true;
            Close();
        }
    }
}

/// <summary>
/// The note on an entry that holds the sheet. A kept path's is the model's own, because whether a
/// folder can be rewritten from here, and whose folder it is, are facts only the model holds; the
/// model arrives as the first bound value, as the editor's rule bindings take it. A lost mark's
/// sentence is a static that names the mark and its connector mid-sentence, which a binding cannot
/// compose without restating the wording here.
/// </summary>
public sealed class UnansweredNoteConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is PublishModel model
            ? values[1] switch
            {
                PublishModel.UnresolvedMark mark => PublishModel.UnresolvedMarkNote(mark.Connector, mark.Name),
                PublishModel.KeptPath kept => model.Note(kept),
                // A container the list has let go carries WPF's disconnected sentinel, not an entry.
                _ => string.Empty,
            }
            : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
