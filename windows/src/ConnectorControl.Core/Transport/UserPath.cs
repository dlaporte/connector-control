namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac puts the tool on PATH with a link in /usr/local/bin.
///
/// The user PATH as Windows keeps it — entries between semicolons, unexpanded — and one folder in it. An
/// entry is the folder whatever its case, quotes, trailing backslash or %VARIABLES%; every other entry is
/// left exactly as it was written.
/// </summary>
public static class UserPath
{
    public static bool Contains(string? path, string folder) =>
        (path ?? "").Split(';').Any(entry => Same(entry, folder));

    public static string Adding(string? path, string folder)
    {
        if (Contains(path, folder))
        {
            return path!;
        }
        var kept = (path ?? "").TrimEnd(';');
        return kept.Length == 0 ? folder : kept + ";" + folder;
    }

    public static string Removing(string? path, string folder) =>
        string.Join(";", (path ?? "").Split(';').Where(entry => !Same(entry, folder)));

    private static bool Same(string entry, string folder) =>
        entry.Trim().Length > 0 && string.Equals(Normal(entry), Normal(folder), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string entry) =>
        Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).TrimEnd('\\', '/');
}
