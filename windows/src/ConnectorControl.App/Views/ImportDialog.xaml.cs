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
        onModelChanged = (_, _) => Refresh();
        Model.PropertyChanged += onModelChanged;
        Closed += (_, _) => Model.PropertyChanged -= onModelChanged;
        Refresh();
    }

    public ImportModel Model { get; }

    /// <summary>
    /// True once the import has landed. The sheet reports through this rather than DialogResult,
    /// whose setter throws on a window presented with Show() — which is how a test drives one.
    /// PublishDialog and ReviewDialog answer the same way, under the same name.
    /// </summary>
    public bool Accepted { get; private set; }

    public static bool Show(Window? owner, ImportModel model)
    {
        var dialog = new ImportDialog(model);
        return Present(dialog, owner, () => dialog.Accepted);
    }

    /// <summary>
    /// The two formatted strings and the one derived flag nothing notifies: the count beside Import
    /// and the mode's own sentence are built from a value rather than being properties, and a row's
    /// tick is a plain property that notifies nobody. Everything that is a property of the model is
    /// bound instead. The Mac's SwiftUI re-reads the whole sheet for free; here the sheet re-reads
    /// what changed with it.
    /// </summary>
    private void Refresh()
    {
        CopiesMode.Content = ImportModel.AddModeTitle(Model.TargetCollection);
        // The radio's sentence is what names the target, so it is the picker's label too.
        AutomationProperties.SetName(TargetBox, ImportModel.AddModeTitle(Model.TargetCollection));
        ImportButton.Content = ImportModel.ImportButton(Model.ImportCount);
        ImportButton.GetBindingExpression(IsEnabledProperty)?.UpdateTarget();
    }

    private void OnRowTicked(object sender, RoutedEventArgs e) => Refresh();

    private void OnImport(object sender, RoutedEventArgs e)
    {
        // The model answers with the reason it could not land, or null. A failure stays on the
        // sheet the user is looking at; the model publishes no property for it, so neither does
        // this line's visibility.
        var failure = Model.Perform();
        FailureText.Text = failure ?? string.Empty;
        FailureText.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
        if (failure is null)
        {
            Accepted = true;
            Close();
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
/// The sheet's two halves: what shows while the document read, and — with <c>error</c> as the
/// parameter — the one line that shows when it did not. A one-sided model flag, the shape
/// <c>FlyoutModel.HasCollectionBanner</c> has, would let the shared Vis converter do this instead.
/// </summary>
public sealed class ImportLoadedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var loaded = value is null;
        var wantsTheError = string.Equals(parameter as string, "error", StringComparison.Ordinal);
        return loaded != wantsTheError ? Visibility.Visible : Visibility.Collapsed;
    }

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

