namespace ConnectorControl.Core.State;

/// <summary>The button words every dialog shares. Mirror: Dialogs.cancelTitle / okTitle (Sources/ConnectorControlState/Dialogs.swift).</summary>
public static class Dialogs
{
    public const string CancelTitle = "Cancel";
    public const string OkTitle = "OK";
}

/// <summary>
/// The Mac NSAlert/confirmationDialog/promptForName surface, as a seam the
/// WPF app implements with owned modal windows and tests script with a fake.
/// </summary>
public interface IDialogs
{
    /// <summary>
    /// Two-button dialog; true when the primary button was chosen. <paramref name="cancelIsDefault"/>
    /// makes the cancel button the one Return presses, for a question whose primary answer is not
    /// to be given by reflex; Escape answers the cancel button either way.
    /// </summary>
    bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle = Dialogs.CancelTitle, bool destructive = false,
                 bool cancelIsDefault = false);

    /// <summary>Text prompt with OK/Cancel; the raw (untrimmed) text, or null on Cancel.</summary>
    string? PromptForName(string title, string initial);

    /// <summary>One-button informational dialog.</summary>
    void Inform(string message, string? informativeText);

    /// <summary>
    /// Update-available dialog; true for "Install and Relaunch", false for "Later". Install and Relaunch
    /// keeps the dialog up, busy and reading "Downloading…", while it awaits <paramref name="install"/>,
    /// which downloads the package and relaunches into it and never throws; the answer comes once it ends.
    /// </summary>
    bool OfferUpdate(string newVersion, string currentVersion, string? notesMarkdown, Func<Task> install);

    /// <summary>The confirmation of a change Claude asks for, modal: Don’t Allow is the default, which Return
    /// and Escape answer, and so does closing the window. True only for Allow.</summary>
    bool ConfirmChange(ConfirmationDialog dialog);
}
