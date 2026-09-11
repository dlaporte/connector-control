namespace ConnectorControl.Core;

/// <summary>
/// Which of the four tools a connector's command needs: the first token by
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
    public static Tool? RequiredTool(JsonValue config) =>
        CommandLine.TryRead(config, out var command, out var args) ? RequiredTool(command, args) : null;

    /// <summary>
    /// Every tool the given configs need, deduplicated and in <see cref="ToolInfo.All"/> order —
    /// what the flyout must have probed before it can decide which rows carry a warning.
    /// </summary>
    public static IReadOnlyList<Tool> RequiredTools(IEnumerable<JsonValue> configs)
    {
        var needed = new HashSet<Tool>();
        foreach (var config in configs)
        {
            if (RequiredTool(config) is { } tool)
            {
                needed.Add(tool);
            }
        }
        return ToolInfo.All.Where(needed.Contains).ToArray();
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
