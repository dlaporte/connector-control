using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeDialogs : IDialogs
{
    public sealed record ConfirmCall(string Message, string? Informative, string Primary, string Cancel, bool Destructive,
                                     bool CancelIsDefault = false);
    public sealed record PromptCall(string Title, string Initial);
    public sealed record InformCall(string Message, string? Informative);
    public sealed record OfferCall(string NewVersion, string CurrentVersion, string? Notes);

    public bool NextConfirm { get; set; } = true;
    /// <summary>
    /// Answers for a flow that raises more than one confirmation, taken in order; NextConfirm
    /// answers whatever is left. One flag cannot say "delete it, but keep the file".
    /// </summary>
    public Queue<bool> ConfirmAnswers { get; } = new();
    /// <summary>Run while a confirmation is up, before it is answered: something that changes under a modal question.</summary>
    public Action? DuringConfirm { get; set; }
    public string? NextPromptAnswer { get; set; }
    public bool NextOffer { get; set; }
    public Exception? OfferFailure { get; set; }
    public List<ConfirmCall> Confirms { get; } = [];
    public List<PromptCall> Prompts { get; } = [];
    public List<InformCall> Informs { get; } = [];
    public List<OfferCall> Offers { get; } = [];
    /// <summary>What ConfirmChange answers: Don’t Allow unless a test says otherwise.</summary>
    public bool NextChangeAnswer { get; set; }
    /// <summary>Run while a change's confirmation is up, before it is answered.</summary>
    public Action? DuringChange { get; set; }
    /// <summary>Thrown by ConfirmChange, once recorded, when set: a dialog that fails once it may be showing.</summary>
    public Exception? ChangeFailure { get; set; }
    public List<ConfirmationDialog> ChangeQuestions { get; } = [];

    public bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle, bool destructive,
                        bool cancelIsDefault)
    {
        Confirms.Add(new ConfirmCall(message, informativeText, primaryTitle, cancelTitle, destructive, cancelIsDefault));
        DuringConfirm?.Invoke();
        return ConfirmAnswers.Count > 0 ? ConfirmAnswers.Dequeue() : NextConfirm;
    }

    public string? PromptForName(string title, string initial)
    {
        Prompts.Add(new PromptCall(title, initial));
        return NextPromptAnswer;
    }

    public void Inform(string message, string? informativeText) => Informs.Add(new InformCall(message, informativeText));

    /// <summary>Run once Install and Relaunch's work has finished, before the dialog answers: what is true while it is still up.</summary>
    public Action? AfterInstall { get; set; }

    public bool OfferUpdate(string newVersion, string currentVersion, string? notesMarkdown, Func<Task> install)
    {
        Offers.Add(new OfferCall(newVersion, currentVersion, notesMarkdown));
        if (OfferFailure is not null)
        {
            throw OfferFailure;
        }
        if (NextOffer)
        {
            // The dialog awaits this with its message loop running; here a blocking wait stands in, which is safe
            // because FakeUpdater finishes (or throws) synchronously and the rigs that accept an offer marshal inline.
            install().GetAwaiter().GetResult();
            AfterInstall?.Invoke();
        }
        return NextOffer;
    }

    public bool ConfirmChange(ConfirmationDialog dialog)
    {
        ChangeQuestions.Add(dialog);
        DuringChange?.Invoke();
        if (ChangeFailure is not null)
        {
            throw ChangeFailure;
        }
        return NextChangeAnswer;
    }
}
