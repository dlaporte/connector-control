using System.Text;

namespace ConnectorControl.Core.State;

/// <summary>
/// A piece of a sentence, and whether it is set apart from the words around it: a name someone
/// else chose, in a confirmation, or a command or path in a caption. The views draw a set-apart
/// piece in the monospaced face, so where it begins and ends shows, whatever it holds. Pieces are
/// found scalar by scalar, as the Mac mirror finds them, so both split a sentence alike.
///
/// Mirror: Sources/ConnectorControlState/TextRun.swift
/// </summary>
public readonly record struct TextRun(string Text, bool IsSetApart = false)
{
    /// <summary>What a name cut to the limit ends with.</summary>
    public const string CutMark = "…";
    private const int OpeningQuote = '\u201C';
    private const int ClosingQuote = '\u201D';

    // Platform idiom: WPF's text engine predates the isolates the Mac uses (U+2068 … U+2069); embedding the piece
    // left to right (U+202A … U+202C) keeps the neutral text between two pieces on the paragraph's side as well.
    // U+2068 takes the direction of the piece's first strong letter where U+202A sets left to right, so a name that
    // mixes directions reads in a different order inside its piece on each platform.
    /// <summary>The text as a view draws it: a set-apart piece embedded left to right, so a name that runs right to
    /// left cannot pull the quotes or the names beside it into its own order; words as they are.</summary>
    public string ShownText => IsSetApart ? "\u202A" + Text + "\u202C" : Text;

    // Platform idiom: a record struct compares its text ordinally by itself, as the Mac mirror's == compares it
    // scalar by scalar; Swift's own == would hold canonically equivalent text equal.

    /// <summary><paramref name="sentence"/> with every occurrence of each of <paramref name="settingApart"/> set apart — at each
    /// place, the first of them found there — and the rest as words. An empty piece sets nothing apart.</summary>
    public static IReadOnlyList<TextRun> Runs(string sentence, IReadOnlyList<string> settingApart)
    {
        var wanted = settingApart.Where(piece => piece.Length > 0).Select(piece => ControlText.Scalars(piece).ToList()).ToList();
        var scalars = ControlText.Scalars(sentence).ToList();
        var runs = new List<TextRun>();
        var words = new List<int>();
        var index = 0;
        while (index < scalars.Count)
        {
            var at = index;
            var piece = wanted.FirstOrDefault(candidate => at + candidate.Count <= scalars.Count
                && scalars.Skip(at).Take(candidate.Count).SequenceEqual(candidate));
            if (piece is not null)
            {
                Append(runs, new TextRun(Join(words)));
                words.Clear();
                runs.Add(new TextRun(Join(piece), IsSetApart: true));
                index += piece.Count;
            }
            else
            {
                words.Add(scalars[index]);
                index++;
            }
        }
        Append(runs, new TextRun(Join(words)));
        return runs;
    }

    /// <summary>One of the app's sentences with every name between its own quotes set apart: the text between
    /// each “ and the ” after it. A name's own “ and ” are escaped before it reaches a sentence, so every raw
    /// one is the sentence's. A name longer than <paramref name="limit"/> scalars is cut to <c>limit - 1</c> and
    /// <see cref="CutMark"/>; an empty name, or a “ never closed, stays words.</summary>
    public static IReadOnlyList<TextRun> Names(string sentence, int limit)
    {
        var runs = new List<TextRun>();
        var words = new List<int>();
        List<int>? name = null;
        foreach (var scalar in ControlText.Scalars(sentence))
        {
            if (name is null)
            {
                words.Add(scalar);
                if (scalar == OpeningQuote)
                {
                    name = [];
                }
                continue;
            }
            if (scalar != ClosingQuote)
            {
                name.Add(scalar);
                continue;
            }
            if (name.Count > 0)
            {
                Append(runs, new TextRun(Join(words)));
                words.Clear();
                runs.Add(new TextRun(Join(Cut(name, limit)), IsSetApart: true));
            }
            words.Add(scalar);
            name = null;
        }
        words.AddRange(name ?? []);
        Append(runs, new TextRun(Join(words)));
        return runs;
    }

    /// <summary><paramref name="name"/> no longer than <paramref name="limit"/> scalars, the last of them <see cref="CutMark"/> when it was longer.</summary>
    internal static List<int> Cut(List<int> name, int limit) =>
        name.Count <= limit ? name : [.. name.Take(limit - 1), .. ControlText.Scalars(CutMark)];

    // Platform idiom: Join, where the Mac mirror says text(_:), since here Text is the record's own property.
    /// <summary>The scalars as text.</summary>
    internal static string Join(IEnumerable<int> scalars)
    {
        var text = new StringBuilder();
        foreach (var scalar in scalars)
        {
            text.Append(ControlText.Text(scalar));
        }
        return text.ToString();
    }

    /// <summary>Adds a run of words to the run of words before it, or a run of its own; an empty one adds nothing.</summary>
    internal static void Append(List<TextRun> runs, TextRun run)
    {
        if (run.Text.Length == 0)
        {
            return;
        }
        if (!run.IsSetApart && runs.Count > 0 && !runs[^1].IsSetApart)
        {
            runs[^1] = new TextRun(runs[^1].Text + run.Text);
        }
        else
        {
            runs.Add(run);
        }
    }
}
