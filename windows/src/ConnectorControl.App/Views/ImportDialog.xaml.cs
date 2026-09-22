using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Import sheet: the document the window's file picker opened, the two exclusive things that
/// can be done with it, and a row per connector saying what would happen to it. Layout, bindings
/// and the refresh a tick or a mode change needs; every rule and string is ImportModel's.
/// </summary>
public partial class ImportDialog : DialogWindow
{
    private readonly PropertyChangedEventHandler onModelChanged;

    public ImportDialog(ImportModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;
        Title = ImportModel.Title;
        // Read once: whether the document could be read is settled before the sheet opens.
        var readable = model.LoadError is null;
        LoadErrorText.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
        Body.Visibility = readable ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = readable ? Visibility.Visible : Visibility.Collapsed;
        onModelChanged = (_, _) => Refresh();
        Model.PropertyChanged += onModelChanged;
        Closed += (_, _) => Model.PropertyChanged -= onModelChanged;
        Refresh();
    }

    public ImportModel Model { get; }

    public static bool Show(Window? owner, ImportModel model)
    {
        var dialog = new ImportDialog(model);
        return Present(dialog, owner, () => dialog.DialogResult == true);
    }

    /// <summary>
    /// The three things that follow the mode, the target and the ticks. The count and the mode's
    /// own sentence are formatted strings rather than properties, and a row's tick is a plain
    /// property that notifies nobody — the Mac's SwiftUI re-reads the whole sheet for free, and
    /// here the sheet re-reads what changed with it.
    /// </summary>
    private void Refresh()
    {
        var copies = Model.ImportMode == ImportModel.Mode.AddToCollection;
        CopiesMode.Content = ImportModel.AddModeTitle(Model.TargetCollection);
        // The radio's sentence is what names the target, so it is the picker's label too.
        AutomationProperties.SetName(TargetBox, ImportModel.AddModeTitle(Model.TargetCollection));
        ImportButton.Content = ImportModel.ImportButton(Model.ImportCount);
        CopiesBody.Visibility = copies ? Visibility.Visible : Visibility.Collapsed;
        SyncBody.Visibility = copies ? Visibility.Collapsed : Visibility.Visible;
        ImportButton.GetBindingExpression(IsEnabledProperty)?.UpdateTarget();
    }

    private void OnRowTicked(object sender, RoutedEventArgs e) => Refresh();

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var failure = Model.Perform();
        FailureText.Text = failure ?? string.Empty;
        FailureText.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
        if (failure is null)
        {
            // Set only for a sheet shown with ShowDialog, which is the only way it is presented.
            DialogResult = true;
        }
    }
}

/// <summary>
/// One collision choice's picker label, from <see cref="ImportModel.ChoiceTitle"/> — the row's
/// picker holds the choices themselves, so the titles are read off them here rather than kept as
/// a second list beside them.
/// </summary>
public sealed class ImportChoiceTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ImportChoice choice ? ImportModel.ChoiceTitle(choice) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Whether a row can be ticked at all: a connector this platform has no way to run carries the
/// reason it cannot, and there is nothing about it left to decide.
/// </summary>
public sealed class ImportCanIncludeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// A row's badge for the reason this platform cannot run its connector, from
/// <see cref="ImportModel.SkippedBadge"/>. The row carries the reason; the sentence around it is
/// the model's, so the badge is composed here rather than written out in the template.
/// </summary>
public sealed class ImportSkippedBadgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string reason ? ImportModel.SkippedBadge(reason) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
