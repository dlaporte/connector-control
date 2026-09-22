using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
/// The Publish sheet and, with one flag flipped, the Export sheet: layout, bindings and the two
/// native pickers. Every rule and string is PublishModel's, and so is every refresh — a tick or a
/// keystroke reaches the preview because the row it changed raises it, not because this window
/// nudges a binding.
/// </summary>
public partial class PublishDialog : DialogWindow
{
    public PublishDialog(PublishModel model, PublishDialogMode mode)
    {
        InitializeComponent();
        Model = model;
        Mode = mode;
        DataContext = model;
        Title = mode == PublishDialogMode.Publish
            ? model.SheetTitle
            : PublishModel.ExportTitle(model.Collection);
        TitleText.Text = Title;
        FolderRow.Visibility = mode == PublishDialogMode.Publish ? Visibility.Visible : Visibility.Collapsed;
        PublishButton.Visibility = mode == PublishDialogMode.Publish ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = mode == PublishDialogMode.Export ? Visibility.Visible : Visibility.Collapsed;
        PublishButton.IsDefault = mode == PublishDialogMode.Publish;
        ExportButton.IsDefault = mode == PublishDialogMode.Export;
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

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose", Multiselect = false };
        if (picker.ShowDialog(this) == true)
        {
            Model.Folder = picker.FolderName;
            ShowFailure(null);
        }
    }

    /// <summary>Each unresolved mark's line is bound to its connector's name, which is all the model needs.</summary>
    private void OnForgetMark(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is string connector)
        {
            Model.ForgetUnresolvedMark(connector);
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
            return;
        }
        // A first publish mints the collection's origin even when the write it then attempts
        // fails, and the footer is the one line that shows it. It derives from no row, so nothing
        // raises it; the sheet the user is left looking at asks for it once, here.
        FooterText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
    }

    private void ShowFailure(string? failure)
    {
        FailureText.Text = failure ?? string.Empty;
        FailureText.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// An unresolved mark's line, from the connector name the list holds. The sentence is the model's
/// static and names the connector mid-sentence, so a binding cannot compose it from parts without
/// restating the wording here.
/// </summary>
public sealed class MarkNoteConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string connector ? PublishModel.UnresolvedMarkNote(connector) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
