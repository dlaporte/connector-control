namespace ConnectorControl.Core;

/// <summary>
/// Which of the four tools a connector's command needs (spec §3.3): the first token by
/// basename, case-insensitive, <c>.cmd</c>/<c>.exe</c> stripped, one <c>cmd /c</c> unwrapped.
/// A command written as a path (<c>C:\Program Files\nodejs\npx.cmd</c>) is left alone — the
/// user chose it deliberately and PATH lookup does not apply.
/// </summary>
public static class ToolRequirement
{
    public static Tool? RequiredTool(string command, IReadOnlyList<string> args)
    {
        if (Normalized(command) is not { } first)
        {
            return null;
        }
        if (first == "cmd" && args.Count >= 2 && args[0].Equals("/c", StringComparison.OrdinalIgnoreCase))
        {
            return Normalized(args[1]) is { } inner ? ToolInfo.Parse(inner) : null;
        }
        return ToolInfo.Parse(first);
    }

    /// <summary>The rule applied to a config object's <c>command</c> and string <c>args</c> (any non-string arg empties the list). Non-objects → null.</summary>
    public static Tool? RequiredTool(JsonValue config)
    {
        if (config.Kind != JsonKind.Object || config["command"] is not { Kind: JsonKind.String } command)
        {
            return null;
        }
        var args = new List<string>();
        if (config["args"] is { Kind: JsonKind.Array } raw)
        {
            foreach (var item in raw.ArrayItems)
            {
                if (item.Kind != JsonKind.String)
                {
                    args.Clear();
                    break;
                }
                args.Add(item.StringValue);
            }
        }
        return RequiredTool(command.StringValue, args);
    }

    /// <summary>Lower-cased basename without one trailing <c>.cmd</c>/<c>.exe</c>; null for blank or path-like tokens.</summary>
    internal static string? Normalized(string token)
    {
        var trimmed = token.TrimSpaces();
        if (trimmed.Length == 0 || trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            return null;
        }
        var name = trimmed.ToLowerInvariant();
        foreach (var ext in new[] { ".cmd", ".exe" })
        {
            if (name.EndsWith(ext, StringComparison.Ordinal))
            {
                name = name[..^ext.Length];
                break;
            }
        }
        return name;
    }
}
