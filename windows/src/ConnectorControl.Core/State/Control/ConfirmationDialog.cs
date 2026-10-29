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
        // Platform idiom: the method itself; the Mac mirror passes a closure, which keeps its main-actor isolation.
        CodeKinds = request.Code.Select(Kind).ToList();
        Footer = request.Footer is { } footer ? TextRun.Names(footer, int.MaxValue) : null;
    }

    public IReadOnlyList<TextRun> Title { get; }
    public IReadOnlyList<TextRun> Message { get; }
    public IReadOnlyList<string> Code { get; }
    public IReadOnlyList<LineKind> CodeKinds { get; }
    /// <summary>The closing line; a path in it is set apart whole.</summary>
    public IReadOnlyList<TextRun>? Footer { get; }

    /// <summary>The code lines as the views draw them: a line that holds a right-to-left letter between a
    /// left-to-right override and the pop that ends it (U+202D … U+202C), and any other as it is. Inside
    /// an override every character is laid out left to right, so the order a line is drawn in is the
    /// order it is stored in: letters that run right to left cannot carry the numbers and arguments
    /// beside them into another order, as a bidi run otherwise would. A line with no such letter is
    /// already drawn in its stored order, so it is left alone and copies from the dialog as it is. A line
    /// holds neither character raw, since both are escaped before it gets here, so nothing in it can end
    /// the override early. Drawing only: <see cref="Code"/>, and the request Allow compares, keep the lines as they are.</summary>
    public IReadOnlyList<string> ShownCode =>
        Code.Select(line => HasRightToLeft(line) ? "\u202D" + line + "\u202C" : line).ToList();

    /// <summary>Whether <paramref name="line"/> holds a character of the blocks where the strong right-to-left letters
    /// are — Hebrew, Arabic, Syriac, Thaana, NKo and the rest to U+08FF, their presentation forms, and their
    /// ranges beyond the Basic Plane. Explicit ranges, the same as the Mac mirror's, so neither platform leans
    /// on its own Unicode tables; a mark or digit inside them only wraps a line that needed no wrapper, which
    /// changes nothing it shows.</summary>
    internal static bool HasRightToLeft(string line) => ControlText.Scalars(line).Any(scalar =>
        scalar is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
            or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF));

    /// <summary>A line a change removes or adds, by the prefix the change lines give it — at the line's start,
    /// or after the one indent an update's lines take under their heading; any other is plain. Only a
    /// change line starts so: a line that continues a field is padded past the widest label, deeper
    /// than either. A key that itself reads as a change line is coloured as one, which says nothing its
    /// own "−" or "+" does not.</summary>
    internal static LineKind Kind(string line) =>
        Starts(line, ControlMasking.RemovedPrefix) ? LineKind.Removed
        : Starts(line, ControlMasking.AddedPrefix) ? LineKind.Added
        : LineKind.Plain;

    /// <summary>Whether <paramref name="line"/> starts with <paramref name="prefix"/>, or with one indent and then
    /// <paramref name="prefix"/>, ordinally.</summary>
    internal static bool Starts(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.Ordinal) || line.StartsWith(ControlTools.Indent + prefix, StringComparison.Ordinal);

    // Platform idiom: text is compared ordinally, as the Mac mirror compares it scalar by scalar, and the lists
    // element by element, which a class would otherwise compare by reference.
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
