using System.Globalization;

namespace ConnectorControl.Core;

/// <summary>
/// Mirror: Sources/ConnectorControlCore/FreeName.swift
/// The one "name", "name 2", "name 3", … sequence every place that has to find a name nothing
/// else answers to walks: a copy landing beside a connector, an import's Keep both, a reconcile
/// keeping a connector, and the collection a launch ingest creates.
/// </summary>
public static class FreeName
{
    /// <summary><paramref name="name"/>, or "name 2", "name 3", … - the first one <paramref name="isTaken"/> turns down.
    /// The number is written in the invariant culture, as Swift writes it.</summary>
    public static string First(string name, Func<string, bool> isTaken)
    {
        if (!isTaken(name))
        {
            return name;
        }
        var suffix = 2;
        while (isTaken(Numbered(name, suffix)))
        {
            suffix++;
        }
        return Numbered(name, suffix);
    }

    private static string Numbered(string name, int suffix) =>
        name + " " + suffix.ToString(CultureInfo.InvariantCulture);
}
