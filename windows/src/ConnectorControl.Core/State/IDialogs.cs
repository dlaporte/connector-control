namespace ConnectorControl.Core.State;

/// <summary>
/// The Mac NSAlert/confirmationDialog/promptForName surface, as a seam the
/// WPF app implements with owned modal windows and tests script with a fake.
/// </summary>
public interface IDialogs
{
    /// <summary>Two-button dialog; true when the primary button was chosen.</summary>
    bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle = "Cancel", bool destructive = false);

    /// <summary>Text prompt with OK/Cancel; the raw (untrimmed) text, or null on Cancel.</summary>
    string? PromptForName(string title, string initial);

    /// <summary>One-button informational dialog.</summary>
    void Inform(string message, string? informativeText);

    /// <summary>Update-available dialog; true for "Install and Relaunch", false for "Later".</summary>
    bool OfferUpdate(string newVersion, string currentVersion, string? notesMarkdown);
}
