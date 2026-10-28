using System.Text.Json;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlCommandLine.swift
///
/// What <c>connector-control</c> was asked to do. This mirror has no enum payloads, so its cases are
/// records; <c>Arguments</c> is a JSON object, which both platforms compare by value.
/// </summary>
public abstract record ControlInvocation
{
    private ControlInvocation()
    {
    }

    public sealed record Help : ControlInvocation;

    public sealed record Version : ControlInvocation;

    public sealed record Relay : ControlInvocation;

    public sealed record Call(string Tool, JsonValue Arguments, bool Json) : ControlInvocation;

    /// <summary>Nothing the tool can run: the sentence to print, and exit 2.</summary>
    public sealed record Usage(string Message) : ControlInvocation;
}

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlCommandLine.swift
///
/// <c>connector-control</c>'s arguments, read into what to run: one command per tool, as <c>help</c> lists
/// them, or <c>help</c>, <c>--version</c> and the relay. <c>--json</c> anywhere asks for the structure
/// Claude receives instead of text — except in <c>add</c> and <c>edit</c>, where <c>--json</c> followed by
/// a JSON object (its first character not a space is "{") is the connector's config. Words after
/// <c>--</c> are names. Paths are made full here, against the directory the command runs in, because the
/// app runs elsewhere.
/// </summary>
public static class ControlCommandLine
{
    public const string JsonFlag = "--json";
    public const string VersionFlag = "--version";
    public static readonly IReadOnlySet<string> HelpWords = new HashSet<string> { "help", "--help", "-h" };
    public const string EndOfOptions = "--";
    private const string Collection = "--collection";
    private const string File = "--file";
    private const string Rename = "--rename";
    private const string Into = "--into";
    private const string New = "--new";
    private const string OnClash = "--on-clash";
    private const string NameOption = "--name";
    private const string Reveal = "--reveal";
    private const string Off = "--off";

    public static string UnknownCommandError(string command) => $"There’s no command named “{command}”. Run connector-control help to see them all.";

    public static string UsageError(string usage) => $"Usage: connector-control {usage}";

    public static string UnknownOptionError(string option) => $"“{option}” isn’t an option of this command.";

    public static string MissingValueError(string option) => $"“{option}” needs a value.";

    public static string VersionLine(string version) => $"connector-control {version}";

    /// <summary>Reads <paramref name="arguments"/>, the program's name left out. <paramref name="workingDirectory"/>
    /// makes a relative path full; <paramref name="readFile"/> reads <c>--file</c>.</summary>
    public static ControlInvocation Parse(IReadOnlyList<string> arguments, string workingDirectory, Func<string, byte[]> readFile)
    {
        var words = arguments.ToList();
        var leadingJson = false;
        while (words.Count > 0 && words[0] == JsonFlag)
        {
            leadingJson = true;
            words.RemoveAt(0);
        }
        if (words.Count == 0)
        {
            return new ControlInvocation.Usage(ControlTools.HelpText());
        }
        var command = words[0];
        var rest = words.Skip(1).ToList();
        // --version wherever --json sits: asking for the structure changes nothing about the version.
        if (arguments.Where(word => word != JsonFlag).SequenceEqual([VersionFlag]))
        {
            return new ControlInvocation.Version();
        }
        if (HelpWords.Contains(command))
        {
            return rest.Count > 0 ? new ControlInvocation.Usage(UnknownOptionError(rest[0])) : new ControlInvocation.Help();
        }
        if (command == ClaudeIntegration.RelayArgument)
        {
            return rest.Count > 0 ? new ControlInvocation.Usage(UnknownOptionError(rest[0])) : new ControlInvocation.Relay();
        }
        try
        {
            var invocation = CommandCall(command, rest, workingDirectory, readFile);
            return leadingJson && invocation is ControlInvocation.Call call ? call with { Json = true } : invocation;
        }
        catch (UsageException ex)
        {
            return new ControlInvocation.Usage(ex.Message);
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    /// <summary>A command's words after its name.</summary>
    private sealed class Words
    {
        public List<string> Positionals { get; } = [];
        public Dictionary<string, string> Options { get; } = [];
        public HashSet<string> Flags { get; } = [];
        public bool Json { get; set; }
        /// <summary><c>add</c> and <c>edit</c>'s config, given after <c>--json</c>.</summary>
        public string? Config { get; set; }
    }

    private static ControlInvocation CommandCall(string command, IReadOnlyList<string> rest, string workingDirectory,
        Func<string, byte[]> readFile)
    {
        string Full(string path) => FullPath(path, workingDirectory);
        JsonValue? ConfigValue(Words words, bool required, string tool) => Config(words, required, tool, workingDirectory, readFile);
        switch (command)
        {
            case "status": return Call("get_status", rest);
            case "doctor": return Call("check_tools", rest);
            case "connectors":
                return Call("list_connectors", rest, options: [Collection], build: w => [("collection", Text(w.Options.GetValueOrDefault(Collection)))]);
            case "show":
                return Call("get_connector", rest, options: [Collection], flags: [Reveal], min: 1, max: 1, build: w =>
                [
                    ("name", JsonValue.String(w.Positionals[0])), ("collection", Text(w.Options.GetValueOrDefault(Collection))),
                    ("reveal", w.Flags.Contains(Reveal) ? JsonValue.Bool(true) : null),
                ]);
            case "enable":
            case "disable":
                return Call("set_connector_enabled", rest, min: 1, max: 1, build: w =>
                    [("name", JsonValue.String(w.Positionals[0])), ("enabled", JsonValue.Bool(command == "enable"))]);
            case "add":
                return Call("add_connector", rest, options: [Collection, File], flags: [Off], takesConfig: true, min: 1, max: 1, build: w =>
                [
                    ("name", JsonValue.String(w.Positionals[0])), ("config", ConfigValue(w, true, "add_connector")),
                    ("collection", Text(w.Options.GetValueOrDefault(Collection))), ("enabled", w.Flags.Contains(Off) ? JsonValue.Bool(false) : null),
                ]);
            case "edit":
                return Call("update_connector", rest, options: [Collection, File, Rename], takesConfig: true, min: 1, max: 1, build: w =>
                [
                    ("name", JsonValue.String(w.Positionals[0])), ("config", ConfigValue(w, false, "update_connector")),
                    ("newName", Text(w.Options.GetValueOrDefault(Rename))), ("collection", Text(w.Options.GetValueOrDefault(Collection))),
                ]);
            case "delete":
                return Call("delete_connectors", rest, options: [Collection], min: 1, max: int.MaxValue, build: w =>
                [
                    ("names", JsonValue.Array(w.Positionals.Select(JsonValue.String))), ("collection", Text(w.Options.GetValueOrDefault(Collection))),
                ]);
            case "collections": return Call("list_collections", rest);
            case "use":
                return Call("switch_collection", rest, min: 1, max: 1, build: w => [("collection", JsonValue.String(w.Positionals[0]))]);
            case "collection":
                var more = rest.Skip(1).ToList();
                switch (rest.FirstOrDefault() ?? "")
                {
                    case "new": return Call("create_collection", more, min: 1, max: 1, build: w => [("name", JsonValue.String(w.Positionals[0]))]);
                    case "rename":
                        return Call("rename_collection", more, min: 2, max: 2, build: w =>
                            [("collection", JsonValue.String(w.Positionals[0])), ("newName", JsonValue.String(w.Positionals[1]))]);
                    case "duplicate":
                        return Call("duplicate_collection", more, min: 2, max: 2, build: w =>
                            [("collection", JsonValue.String(w.Positionals[0])), ("newName", JsonValue.String(w.Positionals[1]))]);
                    case "delete": return Call("delete_collection", more, min: 1, max: 1, build: w => [("collection", JsonValue.String(w.Positionals[0]))]);
                    default: return new ControlInvocation.Usage(UnknownCommandError(string.Join(" ", new[] { command }.Concat(rest.Take(1)))));
                }
            case "import":
                return Call("import_document", rest, options: [Into, New, OnClash], min: 1, max: 1, build: w =>
                [
                    ("path", JsonValue.String(Full(w.Positionals[0]))), ("into", Text(w.Options.GetValueOrDefault(Into))),
                    ("newCollection", Text(w.Options.GetValueOrDefault(New))), ("onClash", Text(w.Options.GetValueOrDefault(OnClash))),
                ]);
            case "subscribe":
                return Call("subscribe", rest, options: [NameOption], min: 1, max: 1, build: w =>
                    [("path", JsonValue.String(Full(w.Positionals[0]))), ("name", Text(w.Options.GetValueOrDefault(NameOption)))]);
            case "updates":
                return Call("get_pending_update", rest, min: 1, max: 1, build: w => [("collection", JsonValue.String(w.Positionals[0]))]);
            case "apply-update":
                return Call("apply_update", rest, min: 1, max: 1, build: w => [("collection", JsonValue.String(w.Positionals[0]))]);
            case "refresh":
                return Call("refresh_subscription", rest, min: 1, max: 1, build: w => [("collection", JsonValue.String(w.Positionals[0]))]);
            case "settings":
                if (rest.FirstOrDefault() != "set")
                {
                    return Call("get_settings", rest);
                }
                return Call("set_setting", rest.Skip(1).ToList(), min: 2, max: 2, build: w =>
                    [("key", JsonValue.String(w.Positionals[0])), ("value", JsonValue.String(w.Positionals[1]))]);
            case "backups":
                if (rest.FirstOrDefault() != "restore")
                {
                    return Call("list_backups", rest);
                }
                return Call("restore_backup", rest.Skip(1).ToList(), min: 1, max: 1, build: w => [("id", JsonValue.String(w.Positionals[0]))]);
            case "restart-claude": return Call("restart_claude", rest);
            default: return new ControlInvocation.Usage(UnknownCommandError(command));
        }
    }

    /// <summary>One command: the tool it runs, what it takes, and how its words become the tool's arguments
    /// (an argument left null is left out).</summary>
    private static ControlInvocation Call(string tool, IReadOnlyList<string> words, HashSet<string>? options = null,
        HashSet<string>? flags = null, bool takesConfig = false, int min = 0, int max = 0,
        Func<Words, IReadOnlyList<(string Key, JsonValue? Value)>>? build = null)
    {
        var read = Read(words, options ?? [], flags ?? [], takesConfig);
        if (read.Positionals.Count < min || read.Positionals.Count > max)
        {
            throw new UsageException(UsageError(UsageOf(tool)));
        }
        var arguments = new List<KeyValuePair<string, JsonValue>>();
        foreach (var (key, value) in build?.Invoke(read) ?? [])
        {
            if (value is not null)
            {
                arguments.Add(new(key, value));
            }
        }
        return new ControlInvocation.Call(tool, JsonValue.Object(arguments), read.Json);
    }

    private static Words Read(IReadOnlyList<string> words, HashSet<string> options, HashSet<string> flags, bool takesConfig)
    {
        var read = new Words();
        var index = 0;
        var namesOnly = false;
        while (index < words.Count)
        {
            var word = words[index];
            index++;
            // Ordinal, so a combining mark after the dashes cannot join them into something else; the Mac
            // mirror reads UTF-8 for the same reason.
            if (namesOnly || !word.StartsWith("--", StringComparison.Ordinal))
            {
                read.Positionals.Add(word);
            }
            else if (word == EndOfOptions)
            {
                namesOnly = true;
            }
            else if (word == JsonFlag)
            {
                if (takesConfig && read.Config is null && index < words.Count && StartsWithBrace(words[index]))
                {
                    read.Config = words[index];
                    index++;
                }
                else
                {
                    read.Json = true;
                }
            }
            else if (flags.Contains(word))
            {
                read.Flags.Add(word);
            }
            else if (options.Contains(word))
            {
                if (index >= words.Count)
                {
                    throw new UsageException(MissingValueError(word));
                }
                read.Options[word] = words[index];
                index++;
            }
            else
            {
                throw new UsageException(UnknownOptionError(word));
            }
        }
        return read;
    }

    /// <summary>Whether <paramref name="word"/>'s first character that is not a space, tab or line break is "{".
    /// Platform idiom: the four ASCII spaces only, which both platforms read alike.</summary>
    private static bool StartsWithBrace(string word) => word.FirstOrDefault(ch => ch is not (' ' or '\t' or '\r' or '\n')) == '{';

    /// <summary><c>add</c> and <c>edit</c>'s config: after <c>--json</c>, or read from <c>--file</c>; not both.</summary>
    private static JsonValue? Config(Words words, bool required, string tool, string workingDirectory, Func<string, byte[]> readFile)
    {
        var path = words.Options.GetValueOrDefault(File);
        if (words.Config is not null && path is not null)
        {
            throw new UsageException(UsageError(UsageOf(tool)));
        }
        if (words.Config is { } inline)
        {
            return ParseConfig(System.Text.Encoding.UTF8.GetBytes(inline));
        }
        if (path is not null)
        {
            var full = FullPath(path, workingDirectory);
            byte[] data;
            try
            {
                data = readFile(full);
            }
            catch (Exception ex)
            {
                throw new UsageException(AppState.SourceUnreadableError(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)), AppState.Friendly(ex)));
            }
            return ParseConfig(data);
        }
        return required ? throw new UsageException(UsageError(UsageOf(tool))) : null;
    }

    private static JsonValue ParseConfig(byte[] data)
    {
        try
        {
            return JsonValue.Parse(data);
        }
        catch (JsonException)
        {
            throw new UsageException(EditorModel.NotValidJson);
        }
    }

    /// <summary>A path as the app must read it. Platform idiom: Path.GetFullPath(path, basePath); the Mac
    /// mirror standardises a file URL relative to the working directory.</summary>
    private static string FullPath(string path, string workingDirectory)
    {
        try
        {
            return Path.GetFullPath(path, workingDirectory);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static JsonValue? Text(string? value) => value is null ? null : JsonValue.String(value);

    private static string UsageOf(string tool) => ControlTools.Catalog.FirstOrDefault(definition => definition.Name == tool)?.CliUsage ?? tool;
}

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlCommandLine.swift (ControlOutput)
///
/// What the command-line tool prints and how it exits: the app's text for people, or the structure Claude
/// receives under <c>--json</c>; a refusal's sentence on stderr.
/// </summary>
public sealed record ControlOutput(string? Stdout, string? Stderr, int ExitCode)
{
    public const int DoneExit = 0;
    public const int RefusedExit = 1;
    public const int UsageExit = 2;
    public const int UnreachableExit = 3;

    public static ControlOutput Usage(string message) => new(null, message, UsageExit);

    public static ControlOutput Unreachable(string message) => new(null, message, UnreachableExit);

    /// <summary>The app's answer to the command's call. A refusal, or an error the session answered instead,
    /// exits 1 with its sentence on stderr — or as <c>{"error": …}</c> on stdout under <paramref name="json"/>.</summary>
    public static ControlOutput Answer(JsonValue response, bool json)
    {
        if (response.Kind != JsonKind.Object)
        {
            return Refused(McpSession.InternalErrorMessage, json);
        }
        if (response["result"] is { Kind: JsonKind.Object } result)
        {
            var structured = result["structuredContent"] ?? JsonValue.Object();
            var text = "";
            if (result["content"] is { Kind: JsonKind.Array } content && content.ArrayItems.Length > 0
                && content.ArrayItems[0] is { Kind: JsonKind.Object } first && first["text"] is { Kind: JsonKind.String } words)
            {
                text = words.StringValue;
            }
            var isRefusal = result["isError"] == JsonValue.Bool(true);
            if (json)
            {
                return new ControlOutput(structured.CompactLine(escapeSlashes: false), null, isRefusal ? RefusedExit : DoneExit);
            }
            return isRefusal ? new ControlOutput(null, text, RefusedExit) : new ControlOutput(text, null, DoneExit);
        }
        var message = McpSession.InternalErrorMessage;
        if (response["error"] is { Kind: JsonKind.Object } error && error["message"] is { Kind: JsonKind.String } said)
        {
            message = said.StringValue;
        }
        return Refused(message, json);
    }

    private static ControlOutput Refused(string message, bool json) =>
        json ? new ControlOutput(JsonValue.Object(("error", JsonValue.String(message))).CompactLine(escapeSlashes: false), null, RefusedExit)
            : new ControlOutput(null, message, RefusedExit);
}
