namespace ConnectorControl.Core;

/// <summary>
/// How one field of a connector is named to the author.
///
/// The document names a field by its own shape — <c>local.args[1]</c>, <c>remote.auth.clientId</c> —
/// and that shape need not be the one the author edits: a connector the document calls remote opens
/// in the local form whenever its command line is not the canonical shape the remote form rebuilds.
/// Where the editor opens a connector in the local form, a field is named as that form shows it;
/// anywhere else the document's own name is given, and said to be the document's, rather than a name
/// the author will not find.
///
/// Mirror: Sources/ConnectorControlCore/FieldName.swift
/// </summary>
public static class FieldName
{
    public const string Command = "the command";

    /// <summary>Counted from one, as the editor lists the argument rows.</summary>
    public static string Argument(int number) => $"argument {number}";

    public static string EnvValue(string name) => $"the value of {name}";

    /// <summary>A hint belongs to the Publish dialog, whichever form the editor opens.</summary>
    public static string Hint(string name) => $"the hint for {name}";

    public static string Document(string field) => $"the document's {field}";

    /// <summary>
    /// <paramref name="field"/>, as <see cref="CollectionDocument.Findings"/> names it, in the words
    /// of the form the editor opens <paramref name="config"/> in. <paramref name="value"/> is the
    /// path reported there, which finds the argument holding it when the document's field belongs to
    /// a form the editor does not show.
    /// </summary>
    public static string Of(string field, JsonValue config, string value)
    {
        if (Between(field, "env.", ".hint") is { } variable)
        {
            return Hint(variable);
        }
        if (Between(field, "needs.", ".hint") is { } need)
        {
            return Hint(need);
        }
        // The editor's own rule for an existing connector: the remote form only for the command line
        // it can rebuild, and the local form for everything else.
        if (RemotePattern.Detect(config) is not null)
        {
            return Document(field);
        }
        if (field == "local.command")
        {
            return Command;
        }
        if (Between(field, "local.args[", "]") is { } index && int.TryParse(index, out var at))
        {
            return Argument(at + 1);
        }
        if (Between(field, "env.", ".value") is { } shared)
        {
            return EnvValue(shared);
        }
        // A remote field on a connector the editor opens locally: the text sits in the command line,
        // where the author edits it. Anything else — an additional field, the platform — has no row
        // of its own, and the document's name is the honest one.
        if (!field.StartsWith("remote.", StringComparison.Ordinal) || config.Kind != JsonKind.Object)
        {
            return Document(field);
        }
        var number = 0;
        if (config["args"] is { Kind: JsonKind.Array } args)
        {
            foreach (var item in args.ArrayItems)
            {
                if (item.Kind != JsonKind.String)
                {
                    continue;
                }
                number++;
                if (KeptValue.Holds(item.StringValue, value))
                {
                    return Argument(number);
                }
            }
        }
        return config["command"] is { Kind: JsonKind.String } command && KeptValue.Holds(command.StringValue, value)
            ? Command
            : Document(field);
    }

    private static string? Between(string field, string prefix, string suffix) =>
        field.StartsWith(prefix, StringComparison.Ordinal) && field.EndsWith(suffix, StringComparison.Ordinal)
        && field.Length > prefix.Length + suffix.Length
            ? field[prefix.Length..^suffix.Length]
            : null;
}
