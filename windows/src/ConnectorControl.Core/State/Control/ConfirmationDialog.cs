namespace ConnectorControl.Core.State;

/// <summary>
/// A confirmation as the app draws it, from the question the tools put: the title and message with
/// every name set apart and none longer than <see cref="NameLimit"/>, the lines of what would run, each with its
/// kind, and the closing line. The views draw it with Don’t Allow the default — Return and Escape
/// answer it, and so does closing the window — and every paragraph left to right, whatever a name
/// begins with, so a name that starts right to left cannot turn the sentence around it. What is
/// compared on Allow is the request, never this: the cut is drawing only.
///
/// Mirror: Sources/ConnectorControlState/Control/ConfirmationDialog.swift
/// </summary>
public sealed class ConfirmationDialog : IEquatable<ConfirmationDialog>
{
    public const string AllowButton = "Allow";
    public const string DontAllowButton = "Don’t Allow";
    /// <summary>The longest a name runs, in scalars, before it is cut: a name the length of a paragraph could
    /// push the rest of the question out of sight.</summary>
    public const int NameLimit = 64;

    /// <summary>What a line of a change does: the views colour it, and its own "−" or "+" still says so.</summary>
    public enum LineKind { Plain, Removed, Added }

    public ConfirmationDialog(ConfirmationRequest request)
    {
        Title = TextRun.Names(request.Title, NameLimit);
        Message = TextRun.Names(request.Message, NameLimit);
        Code = request.Code;
        CodeKinds = request.Code.Select(Kind).ToList();
        Footer = request.Footer is { } footer ? TextRun.Names(footer, int.MaxValue) : null;
    }

    public IReadOnlyList<TextRun> Title { get; }
    public IReadOnlyList<TextRun> Message { get; }
    public IReadOnlyList<string> Code { get; }
    public IReadOnlyList<LineKind> CodeKinds { get; }
    /// <summary>The closing line; a path in it is set apart whole.</summary>
    public IReadOnlyList<TextRun>? Footer { get; }

    /// <summary>A line a change removes or adds, by the prefix the change lines give it; any other is plain.</summary>
    internal static LineKind Kind(string line) =>
        line.StartsWith(ControlMasking.RemovedPrefix, StringComparison.Ordinal) ? LineKind.Removed
        : line.StartsWith(ControlMasking.AddedPrefix, StringComparison.Ordinal) ? LineKind.Added
        : LineKind.Plain;

    // Platform idiom: the Mac's struct compares its arrays element by element by itself; a C# class says so here.
    public bool Equals(ConfirmationDialog? other) =>
        other is not null && Title.SequenceEqual(other.Title) && Message.SequenceEqual(other.Message)
        && Code.SequenceEqual(other.Code, StringComparer.Ordinal) && CodeKinds.SequenceEqual(other.CodeKinds)
        && (Footer is null ? other.Footer is null : other.Footer is not null && Footer.SequenceEqual(other.Footer));

    public override bool Equals(object? obj) => Equals(obj as ConfirmationDialog);

    public override int GetHashCode() => HashCode.Combine(Title.Count, Message.Count, Code.Count, Footer?.Count);
}

/// <summary>
/// The app's confirmer: each question goes to the user through <see cref="IDialogs.ConfirmChange"/>, on a
/// turn of its own that <c>present</c> gives it, never inside <see cref="Confirm"/>. So Confirm returns at once, and
/// the call's progress and early answer go out while the dialog is open (see <see cref="IControlConfirmer"/>).
/// <c>completion</c> is called once, with true only for Allow; the dialog's own Don’t Allow, Escape and
/// close all answer false.
///
/// Platform idiom: the Mac's initialiser defaults its presenter to a turn of the main run loop; this assembly
/// cannot name the WPF dispatcher, so the app always passes <c>UiThread.Post</c>.
///
/// Mirror: Sources/ConnectorControlState/Control/ConfirmationDialog.swift
/// </summary>
public sealed class DialogConfirmer(IDialogs dialogs, Action<Action> present) : IControlConfirmer
{
    public void Confirm(ConfirmationRequest request, Action<bool> completion)
    {
        var dialog = new ConfirmationDialog(request);
        present(() =>
        {
            bool allowed;
            try
            {
                allowed = dialogs.ConfirmChange(dialog);
            }
            catch (Exception)
            {
                // Platform idiom: a WPF dialog can throw once it may be showing, which a Swift dialog cannot.
                // That is Don’t Allow, never a throw: the write queue waits for exactly one answer.
                allowed = false;
            }
            completion(allowed);
        });
    }
}
