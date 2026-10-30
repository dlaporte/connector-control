using System.Globalization;

namespace ConnectorControl.Core.State;

/// <summary>
/// Mirror: Sources/ConnectorControlState/Control/ControlVersion.swift
///
/// Versions as the hello carries them and the apps show them, compared by Semantic Versioning's
/// precedence. A version that cannot be read — "development build" — is newer than nothing and
/// older than nothing, so it never decides anything.
/// </summary>
public static class ControlVersion
{
    /// <summary>The SemVer at the start of <paramref name="text"/> — "1.5.0" from the Mac's "1.5.0 (10500)",
    /// "1.5.0-preview.3" from .NET's "1.5.0-preview.3+1a2b3c4" — or null when it starts with none.</summary>
    public static string? Semantic(string text)
    {
        var end = 0;
        for (var part = 0; part < 3; part++)
        {
            if (part > 0)
            {
                if (end >= text.Length || text[end] != '.')
                {
                    return null;
                }
                end++;
            }
            var start = end;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }
            if (end == start)
            {
                return null;
            }
        }
        if (end < text.Length && text[end] == '-')
        {
            var cursor = end + 1;
            while (cursor < text.Length && (char.IsAsciiLetterOrDigit(text[cursor]) || text[cursor] is '.' or '-'))
            {
                cursor++;
            }
            if (cursor > end + 1)
            {
                end = cursor;
            }
        }
        return text[..end];
    }

    /// <summary>Whether <paramref name="one"/> comes after <paramref name="than"/>: major, minor and patch as
    /// numbers; a release after its prereleases; prerelease identifiers numbers by value, below words, words
    /// in ASCII order, and more of them after fewer.</summary>
    public static bool IsNewer(string one, string than)
    {
        if (Parts(one) is not { } a || Parts(than) is not { } b)
        {
            return false;
        }
        for (var i = 0; i < 3; i++)
        {
            if (a.Numbers[i] != b.Numbers[i])
            {
                return a.Numbers[i] > b.Numbers[i];
            }
        }
        switch (a.Prerelease.Length == 0, b.Prerelease.Length == 0)
        {
            case (true, true): return false;
            case (true, false): return true;
            case (false, true): return false;
        }
        for (var i = 0; i < Math.Min(a.Prerelease.Length, b.Prerelease.Length); i++)
        {
            var order = Compare(a.Prerelease[i], b.Prerelease[i]);
            if (order != 0)
            {
                return order > 0;
            }
        }
        return a.Prerelease.Length > b.Prerelease.Length;
    }

    internal static (ulong[] Numbers, string[] Prerelease)? Parts(string text)
    {
        if (Semantic(text) is not { } version)
        {
            return null;
        }
        var pieces = version.Split('-', 2);
        var numbers = new List<ulong>();
        foreach (var piece in pieces[0].Split('.'))
        {
            if (ulong.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number);
            }
        }
        if (numbers.Count != 3)
        {
            return null;
        }
        return (numbers.ToArray(), pieces.Length > 1 ? pieces[1].Split('.') : []);
    }

    /// <summary>One prerelease identifier against another: below zero, zero or above zero. An identifier is a
    /// number only when it is all ASCII digits — "-0" is a word on both platforms — and numbers compare by value
    /// without being parsed, so none is too long to compare.</summary>
    internal static int Compare(string one, string other)
    {
        var a = IsNumber(one);
        var b = IsNumber(other);
        if (a && b)
        {
            var x = one.TrimStart('0');
            var y = other.TrimStart('0');
            return x.Length != y.Length ? (x.Length < y.Length ? -1 : 1) : Math.Sign(string.CompareOrdinal(x, y));
        }
        return (a, b) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => Math.Sign(string.CompareOrdinal(one, other)),
        };
    }

    internal static bool IsNumber(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);
}
