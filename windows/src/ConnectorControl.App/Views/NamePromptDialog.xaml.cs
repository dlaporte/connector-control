using System.Windows;

namespace ConnectorControl.App.Views;

/// <summary>Catalog §1.18 promptForName: a 220-wide text field prefilled with the initial value, OK/Cancel, raw text returned.</summary>
public partial class NamePromptDialog : Window
{
    public NamePromptDialog(string title, string initial)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        NameBox.Text = initial;
        if (TryFindResource("AccentButtonStyle") is Style style)
        {
            OkButton.Style = style;
        }
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The raw (untrimmed) text, or null when cancelled.</summary>
    public string? Result { get; private set; }

    public static string? Show(Window? owner, string title, string initial)
    {
        var dialog = new NamePromptDialog(title, initial);
        WpfDialogs.Present(dialog, owner);
        return dialog.Result;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = NameBox.Text;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
