namespace ConnectorControl.Core;

/// <summary>
/// The <c>command</c> + <c>args</c> boilerplate every JsonValue-driven launcher check needs.
/// A non-object, or a missing/non-string <c>command</c>, fails outright; a missing, non-array,
/// or not-all-strings <c>args</c> degrades to an empty list rather than failing the whole read.
/// That leniency is safe for every current caller: each already treats an empty args list the
/// same as "not recognized" (an npx invocation needs at least one arg, a required-tool lookup
/// is happy to fall back to the command name alone).
/// </summary>
public static class CommandLine
{
    public static bool TryRead(JsonValue config, out string command, out IReadOnlyList<string> args)
    {
        command = "";
        args = [];
        if (config.Kind != JsonKind.Object || config["command"] is not { Kind: JsonKind.String } cmd)
        {
            return false;
        }
        command = cmd.StringValue;
        if (config["args"] is { Kind: JsonKind.Array } raw)
        {
            var list = new List<string>();
            foreach (var item in raw.ArrayItems)
            {
                if (item.Kind != JsonKind.String)
                {
                    return true;   // the command is still good; args degrade to empty rather than failing the read
                }
                list.Add(item.StringValue);
            }
            args = list;
        }
        return true;
    }
}
