using System.Windows;
using ConnectorControl.Core.State;

namespace ConnectorControl.App.Views;

/// <summary>
/// The Copy sheet: the ticked connectors against the collection they are going to, asking only
/// about the ones it already holds. Layout and bindings only; every rule and string is
/// CopyModel's, and the collision picker is the Import sheet's.
/// </summary>
public partial class CopyDialog : DialogWindow
{
    /// <summary>
    /// The refusal a failed copy leaves in the window's model, which this sheet cannot reach
    /// through CopyModel: the copy is the window model's verb, and so is its reason.
    /// </summary>
    private readonly Func<string?> refusal;

    public CopyDialog(CopyModel model, Func<string?> refusal)
    {
        InitializeComponent();
        Model = model;
        this.refusal = refusal;
        DataContext = model;
        Title = CopyModel.Title(model.Destination);
        HeadingText.Text = Title;
    }

    public CopyModel Model { get; }

    /// <summary>
    /// True once the copies have landed. The sheet reports through this rather than DialogResult,
    /// whose setter throws on a window presented with Show() — ImportDialog answers the same way.
    /// </summary>
    public bool Accepted { get; private set; }

    public static bool Show(Window? owner, CopyModel model, Func<string?> refusal)
    {
        var dialog = new CopyDialog(model, refusal);
        return Present(dialog, owner, () => dialog.Accepted);
    }

    /// <summary>
    /// A copy that did not land with nothing to say for itself — the ticks went while the sheet
    /// was up — has nothing left to ask either, so the sheet goes.
    /// </summary>
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var landed = Model.Perform();
        var failure = landed ? null : refusal();
        ShowFailure(FailureText, failure);
        if (failure is null)
        {
            Accepted = landed;
            Close();
        }
    }
}
