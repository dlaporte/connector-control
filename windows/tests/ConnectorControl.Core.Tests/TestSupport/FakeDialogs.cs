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
    public string? NextPromptAnswer { get; set; }
    public bool NextOffer { get; set; }
    public Exception? OfferFailure { get; set; }
    public List<ConfirmCall> Confirms { get; } = [];
    public List<PromptCall> Prompts { get; } = [];
    public List<InformCall> Informs { get; } = [];
    public List<OfferCall> Offers { get; } = [];

    public bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle, bool destructive,
                        bool cancelIsDefault)
    {
        Confirms.Add(new ConfirmCall(message, informativeText, primaryTitle, cancelTitle, destructive, cancelIsDefault));
        return ConfirmAnswers.Count > 0 ? ConfirmAnswers.Dequeue() : NextConfirm;
    }

    public string? PromptForName(string title, string initial)
    {
        Prompts.Add(new PromptCall(title, initial));
        return NextPromptAnswer;
    }

    public void Inform(string message, string? informativeText) => Informs.Add(new InformCall(message, informativeText));

    public bool OfferUpdate(string newVersion, string currentVersion, string? notesMarkdown)
    {
        Offers.Add(new OfferCall(newVersion, currentVersion, notesMarkdown));
        if (OfferFailure is not null)
        {
            throw OfferFailure;
        }
        return NextOffer;
    }
}
