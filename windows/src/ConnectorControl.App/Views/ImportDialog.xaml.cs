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
    /// The two strings a binding cannot carry: the count beside Import and the mode's own sentence
    /// are built from a value rather than being properties of their own. Everything else is bound.
    /// Reached only through the model's own notification — a row raises its edit, the model
    /// re-raises the count and CanImport that follow it, and this runs once for the pair.
    /// </summary>
    private void Refresh()
    {
        CopiesMode.Content = ImportModel.AddModeTitle(Model.TargetCollection);
        // The radio's sentence is what names the target, so it is the picker's label too.
        AutomationProperties.SetName(TargetBox, ImportModel.AddModeTitle(Model.TargetCollection));
        ImportButton.Content = ImportModel.ImportButton(Model.ImportCount);
        ImportButton.GetBindingExpression(IsEnabledProperty)?.UpdateTarget();
    }

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
/// A row control's accessibility name, from the connector it acts on: the tick's
/// <see cref="ImportModel.IncludeLabel"/>, or — with <c>choice</c> as the parameter — the collision
/// picker's <see cref="ImportModel.CollisionPickerLabel"/>. Both are factories over the row's own
/// name, which a DataTemplate cannot call.
/// </summary>
public sealed class ImportRowLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string connector)
        {
            return string.Empty;
        }
        return string.Equals(parameter as string, "choice", StringComparison.Ordinal)
            ? ImportModel.CollisionPickerLabel(connector)
            : ImportModel.IncludeLabel(connector);
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

