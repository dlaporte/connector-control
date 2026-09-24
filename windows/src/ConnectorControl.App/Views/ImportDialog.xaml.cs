using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
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
        onModelChanged = (_, e) =>
        {
            // Another mode is another question: what the last Import said no longer answers it.
            if (ObservableObject.Affects(e, nameof(ImportModel.ImportMode)))
            {
                ShowFailure(FailureText, null);
            }
            Refresh();
        };
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
    /// are built from a value rather than being properties of their own. Everything else is bound,
    /// Import's IsEnabled included. Reached only through the model's own notification — a row, the
    /// mode or the target raises the count and CanImport that follow it.
    /// </summary>
    private void Refresh()
    {
        CopiesMode.Content = ImportModel.AddModeTitle(Model.TargetCollection);
        // The radio's sentence is what names the target, so it is the picker's label too.
        AutomationProperties.SetName(TargetBox, ImportModel.AddModeTitle(Model.TargetCollection));
        ImportButton.Content = ImportModel.ImportButton(Model.ImportCount);
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        // The model answers with the reason it could not land, or null. A failure stays on the
        // sheet the user is looking at.
        var failure = Model.Perform();
        ShowFailure(FailureText, failure);
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
public sealed class ImportChoiceTitleConverter : OneWayConverter<ImportChoice>
{
    protected override object? Map(ImportChoice choice, object? parameter) => ImportModel.ChoiceTitle(choice);
}

/// <summary>
/// A row control's accessibility name, from the connector it acts on: the tick's
/// <see cref="ImportModel.IncludeLabel"/>, or — with <c>choice</c> as the parameter — the collision
/// picker's <see cref="ImportModel.CollisionPickerLabel"/>. Both are factories over the row's own
/// name, which a DataTemplate cannot call.
/// </summary>
public sealed class ImportRowLabelConverter : OneWayConverter<string>
{
    protected override object? Map(string connector, object? parameter) =>
        string.Equals(parameter as string, "choice", StringComparison.Ordinal)
            ? ImportModel.CollisionPickerLabel(connector)
            : ImportModel.IncludeLabel(connector);
}
