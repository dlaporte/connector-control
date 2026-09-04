using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeDialogs : IDialogs
{
    public sealed record ConfirmCall(string Message, string? Informative, string Primary, string Cancel, bool Destructive);
    public sealed record PromptCall(string Title, string Initial);
    public sealed record InformCall(string Message, string? Informative);
    public sealed record OfferCall(string NewVersion, string CurrentVersion, string? Notes);

    public bool NextConfirm { get; set; } = true;
    public string? NextPromptAnswer { get; set; }
    public bool NextOffer { get; set; }
    public List<ConfirmCall> Confirms { get; } = [];
    public List<PromptCall> Prompts { get; } = [];
    public List<InformCall> Informs { get; } = [];
    public List<OfferCall> Offers { get; } = [];

    public bool Confirm(string message, string? informativeText, string primaryTitle, string cancelTitle = "Cancel", bool destructive = false)
    {
        Confirms.Add(new ConfirmCall(message, informativeText, primaryTitle, cancelTitle, destructive));
        return NextConfirm;
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
        return NextOffer;
    }
}
