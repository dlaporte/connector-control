using System.ComponentModel;

namespace ConnectorControl.Core.State;

/// <summary>
/// The Collections window, minus pixels: the collections as items in the left pane, the selected
/// collection's connectors as rows in the right one, the detail line above them, and the controls
/// that follow the selection: the sidebar's +, the header's ⋯ menu, the list header's + and the
/// selection bar. Everything is derived from AppState; the model owns only what the window itself
/// knows — which collection is showing and which rows are ticked.
///
/// Mirror: Sources/ConnectorControlState/CollectionsModel.swift
/// </summary>
public sealed class CollectionsModel : ObservableObject, IDisposable
{
    public const string WindowTitle = "Collections";
    public const string ImportButton = "Import";
    public const string SubscribeButton = "Subscribe";
    public const string PublishButton = "Start Publishing";
    /// <summary>The same sheet, reached from a collection that already publishes.</summary>
    public const string PublishSettingsButton = "Publishing Settings";
    public const string RefreshButton = "Refresh";
    public const string MakeLocalCopyButton = "Make Local Copy";
    public const string NewButton = "New Collection";
    public const string RenameAction = "Rename";
    public const string DeleteAction = "Delete";
    public const string StopPublishingAction = "Stop Publishing";
    public const string StopSyncingAction = "Stop Syncing";
    public const string StopSyncingInformative = "The connectors stay as a local collection you can edit.";
    public static string StopSyncingMessage(string collection) => $"Stop Syncing “{collection}”?";
    public const string ActiveSuffix = " · active";
    public const string UnlocatedDetail = "synced · file not located on this machine";
    /// <summary>
    /// The source's status in the detail line. The cache records no timestamp, so this says what
    /// is true of the file, not how long ago it last changed.
    /// </summary>
    public const string UpdateAvailableStatus = "update available";
    public const string UpToDateStatus = "up to date";
    /// <summary>
    /// The two answers to the published-document question. Keep is the default: a file the team
    /// reads is not something to remove by pressing Return.
    /// </summary>
    public const string RemoveFileButton = "Remove";
    public const string KeepFileButton = "Keep";
    public const string RemoteType = "remote";
    /// <summary>The row's pencil, which names no connector: the row it sits on is the answer.</summary>
    public const string EditTooltip = "Edit";
    /// <summary>The sidebar's double-click, and the same action in its context menu.</summary>
    public const string MakeActiveAction = "Make Active";
    /// <summary>The lock at the head of a synced collection's row, and on the flyout's rows too.</summary>
    public const string LockedGlyphTooltip = "Read-only: synced from the collection's author";
    /// <summary>
    /// The open and save dialogs' filter for a collection document. Windows only: the Mac's
    /// panels filter by content type, which has no wording.
    /// </summary>
    public const string DocumentFilter = "Collection (*.json)|*.json";

    // MARK: selection bar

    public const string CopyToButton = "Copy to";
    public const string ExportCheckedButton = "Export";
    public const string RemoveCheckedButton = "Remove";

    /// <summary>The bar's own tally, e.g. "2 selected".</summary>
    public static string SelectedCount(int n) => $"{n} selected";

    /// <summary>
    /// Names the connector when there is exactly one ticked, and states the count otherwise: a
    /// removal of one row deserves the same specificity the editor's own Remove used to give it,
    /// and a removal of several would only get longer for naming them all.
    /// </summary>
    public static string RemoveCheckedMessage(IReadOnlyList<string> names) =>
        names.Count == 1 ? $"Remove “{names[0]}”?" : $"Remove {names.Count} connectors?";

    /// <summary>
    /// Lifted from the editor's Remove confirmation, which the list now replaces: the sentence —
    /// the most useful thing in that confirmation — survives here unchanged.
    /// </summary>
    public const string RemoveCheckedInformative = "A copy remains in Backups.";

    /// <summary>
    /// The sidebar's chain glyph, or null when there is no chain to explain: a local collection
    /// has no source, and a synced one whose file is still to be found has no path to name. The
    /// sentence is the flyout chip's, borrowed rather than copied — one fact, one wording.
    ///
    /// Takes the item rather than the path so that "which items have a tooltip" stays here; a
    /// converter over an optional path would be the same rule, kept somewhere worse.
    /// </summary>
    public static string? SyncedGlyphTooltip(Item item) =>
        item.Source is { } source ? FlyoutModel.SourceTooltipFormat(source) : null;

    public static string LocalDetail(int count) => $"local · {count} connectors";

    /// <summary><paramref name="source"/> is the document's path on this machine, never the sidecar's origin, which is a UUID.</summary>
    public static string SyncedDetail(string source, string status) => $"synced from {source} · read-only · {status}";

    /// <summary>"this PC" is the platform-forced half of this sentence; the Mac mirror says "this Mac".</summary>
    public static string PublishedDetail(string folder) => $"publishes to {folder} from this PC";

    public static string DeletePublishedFileQuestion(string fileName) => $"Also remove {fileName} from the folder?";

    /// <summary>
    /// One collection in the left pane. A published collection carries no mark of its own there:
    /// publishing is what the detail line and the header's pill say, not a sidebar glyph.
    /// </summary>
    /// <param name="Source">
    /// Where a synced collection's document is, as far as this machine knows: the path it is
    /// bound to, or the name the sidecar recorded while the file is still to be found. Null for
    /// a local collection, which has no source, and for a synced one the sidecar never named.
    /// <c>AppState.SourceLocation</c> is the rule, shared with the flyout's chip and menu, so the
    /// sidebar's chain and the chip cannot name the same collection differently.
    /// </param>
    public sealed record Item(string Name, CollectionKind Kind, bool IsActive,
        bool HasPendingUpdate, bool IsLocated, string? Source = null)
    {
        public string Id => Name;
    }

    /// <summary>
    /// One entry in Copy to's destination menu. Every collection except the one the rows are in
    /// appears; a synced one is listed but cannot take copies, so the menu can say why rather than
    /// hide it.
    /// </summary>
    /// <param name="IsEnabled">False for a synced collection: its connectors are the author's.</param>
    public sealed record CopyDestination(string Name, bool IsEnabled)
    {
        public string Id => Name;
    }

    /// <summary>The menu's annotation beside a disabled destination — why a synced collection is listed but cannot be chosen.</summary>
    public const string ReadOnlyNote = "read-only";

    /// <summary>
    /// One connector of the selected collection. Checked is the window's own state — an export
    /// tick, not anything the store holds — so it is the one field the model fills in itself.
    /// </summary>
    public sealed record Row(string Name, string? Caution, bool IsLocked, bool Checked, string Target)
    {
        public string Id => Name;
    }

    /// <summary>Property names this window actually depends on — everything else AppState raises is noise for it.</summary>
    private static readonly string[] RelevantProperties =
    [
        nameof(AppState.Store), nameof(AppState.CollectionsFile), nameof(AppState.CollectionsCache),
        nameof(AppState.PendingUpdates), nameof(AppState.SourceErrors), nameof(AppState.PublishError),
    ];

    private readonly AppState state;
    private readonly IDialogs dialogs;
    private readonly HashSet<string> checkedNames = new(StringComparer.Ordinal);
    private string? lastError;
    /// <summary>What the view last picked, which may name a collection that no longer exists; Selected resolves it.</summary>
    private string? selection;
    /// <summary>
    /// Which collection the ticks above belong to. The window shows one collection at a time, and
    /// a tick must not survive into another one that happens to hold a connector of that name.
    /// </summary>
    private string? checkedCollection;
    /// <summary>
    /// The two panes, kept rather than rebuilt on every read. WPF regenerates every container when
    /// ItemsSource is handed a new list, which drops keyboard focus from the row that had it — so
    /// a list is replaced, and announced, only when what it holds has actually changed. The Mac
    /// needs none of this: SwiftUI's List diffs its rows by id and keeps an unchanged row's view.
    /// </summary>
    private IReadOnlyList<Item> items = [];
    private IReadOnlyList<Row> rows = [];

    public CollectionsModel(AppState state, IDialogs dialogs)
    {
        this.state = state;
        this.dialogs = dialogs;
        items = ComputeItems();
        rows = ComputeRows();
        state.PropertyChanged += OnStateChanged;
    }

    /// <summary>
    /// What the last action AppState refused reported, cleared by the next one that succeeds, is
    /// cancelled, declined at its confirmation, or finds nothing to do.
    /// </summary>
    public string? LastError { get => lastError; private set => Set(ref lastError, value); }

    // MARK: selection

    /// <summary>
    /// The collection the right pane is showing. It defaults to the active one and falls back to
    /// it whenever the chosen name stops being a collection — deleted here, or renamed from
    /// anywhere else.
    /// </summary>
    /// <remarks>
    /// An assignment that would show the collection already showing does nothing at all — no
    /// raise, no cleared ticks, not even the name remembered. The sidebar's two-way binding writes
    /// its selection straight back, and a raise here fed it until the stack ran out; remembering
    /// the name would pin the window to a collection it was only showing because it was the
    /// active one.
    /// </remarks>
    public string? Selected
    {
        get => SelectedCollection;
        set
        {
            if (EffectiveCollection(value) == SelectedCollection)
            {
                ForgetUnresolvedSelection();
                return;
            }
            selection = value;
            checkedNames.Clear();
            checkedCollection = null;
            RefreshRows();
            RaiseSelectionDependents();
        }
    }

    private string SelectedCollection => EffectiveCollection(selection);

    /// <summary>
    /// Lets go of a remembered name that no longer names a collection. Nothing on screen changes —
    /// the window was already showing the fallback — so nothing is raised. Without it, a collection
    /// renamed away and later renamed back would pull the window to it unprompted, because the
    /// name would start resolving again. Called from the no-op branch of <see cref="Selected"/>
    /// and on every store change, so a view that never writes its selection back is covered too.
    /// </summary>
    private void ForgetUnresolvedSelection()
    {
        if (selection is { } remembered && !state.Store.Collections.ContainsKey(remembered))
        {
            selection = null;
        }
    }

    /// <summary>
    /// Everything the window binds that follows the collection on show, and nothing that does not
    /// — in particular not <see cref="Items"/>, whose content the selection never touches. What
    /// the code-behind reads when a menu opens — <see cref="CopyDestinations"/>,
    /// <see cref="CollectionMenu"/>, <see cref="CanRefresh"/>, <see cref="CanDelete"/>,
    /// <see cref="PublishedFilePath"/> and <see cref="SourceFilePath"/> — is read fresh there, so
    /// nothing listens for it and it is not raised.
    /// </summary>
    private void RaiseSelectionDependents()
    {
        Raise(nameof(Selected));
        Raise(nameof(DetailLine));
        Raise(nameof(BannerText));
        Raise(nameof(BannerButton));
        Raise(nameof(HasBanner));
        Raise(nameof(CanRemoveChecked));
        Raise(nameof(Pills));
        Raise(nameof(CanAddConnector));
        Raise(nameof(AddConnectorTooltipText));
    }

    /// <summary>What a chosen name resolves to: itself while it is a collection, the active one otherwise.</summary>
    private string EffectiveCollection(string? chosen) =>
        chosen is { } name && state.Store.Collections.ContainsKey(name) ? name : state.ActiveCollection;

    /// <summary>The ticks, but only while the collection they were made in is still the one showing.</summary>
    private IReadOnlySet<string> ActiveChecks =>
        checkedCollection == SelectedCollection ? checkedNames : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Moves the window to a collection this model just created or renamed, keeping the ticks with
    /// it — unlike Selected, which is the user picking a different collection.
    /// </summary>
    private void Retarget(string name)
    {
        if (checkedCollection is not null)
        {
            checkedCollection = name;
        }
        selection = name;
        RefreshRows();
        RaiseSelectionDependents();
    }

    // MARK: panes

    public IReadOnlyList<Item> Items => items;

    public IReadOnlyList<Row> Rows => rows;

    private List<Item> ComputeItems()
    {
        var active = state.ActiveCollection;
        return state.CollectionNames
            .Select(name => new Item(name, state.KindOf(name), name == active,
                state.PendingUpdates.ContainsKey(name), state.IsLocated(name), state.SourceLocation(name)))
            .ToList();
    }

    /// <summary>
    /// Both panes, each replaced and announced only when its content differs. The records are
    /// value-equal, so a rebuild that says the same thing leaves the list — and the focus on it —
    /// where it was.
    /// </summary>
    private void RefreshPanes()
    {
        var fresh = ComputeItems();
        if (!fresh.SequenceEqual(items))
        {
            items = fresh;
            Raise(nameof(Items));
        }
        RefreshRows();
    }

    private void RefreshRows()
    {
        var fresh = ComputeRows();
        if (!fresh.SequenceEqual(rows))
        {
            rows = fresh;
            Raise(nameof(Rows));
            Raise(nameof(CheckedNames));
        }
    }

    private List<Row> ComputeRows()
    {
        var collection = SelectedCollection;
        var locked = state.IsSynced(collection);
        var checks = ActiveChecks;
        var mcps = state.Store.Collections.TryGetValue(collection, out var held)
            ? held.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        // Ordinal, which is how the flyout sorts the same connectors of the same collection: two
        // surfaces over one list must agree on its order.
        return mcps.Keys
            .Order(StringComparer.Ordinal)
            .Select(name => new Row(name, state.ConnectorCaution(name, collection),
                locked, checks.Contains(name), TargetOf(mcps[name].Config)))
            .ToList();
    }

    /// <summary>
    /// What a row says the connector runs, never a secret: a remote connector's host; a local
    /// one's launcher and arguments, shortened. Public and pure so both platforms test the same
    /// inputs. <paramref name="home"/> is the user's home folder, abbreviated to "~".
    ///
    /// An allowlist, not a mask: an argument is shown only when it is a URL, an explicit path or
    /// a named artefact, and every other argument — a flag, a flag's value, <c>KEY=value</c>, a
    /// header, a shell string, a bare word — is left out. A list of secret shapes to hide would
    /// leak every shape it did not foresee. The one exception is backed by a name rather than a
    /// shape: whatever follows a flag named for a secret is left out too, since a password can
    /// look like a package.
    /// </summary>
    public static string TargetOf(JsonValue config, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var model = FormMapper.Analyze(config).Model;
        var (command, commandArgs) = SplitCommandLine(model.Command, model.Args);
        // Windows' `cmd /c npx …`: what cmd runs is the launcher, and the rule applies to what
        // follows it.
        if (LauncherName(command).ToLowerInvariant() == "cmd" && commandArgs.Count > 0
            && commandArgs[0].ToLowerInvariant() is "/c" or "/k")
        {
            commandArgs.RemoveAt(0);
            if (commandArgs.Count > 0)
            {
                var inner = commandArgs[0];
                commandArgs.RemoveAt(0);
                (command, commandArgs) = SplitCommandLine(inner, commandArgs);
            }
        }
        var runner = LauncherName(command).ToLowerInvariant();
        // Read through the unwrapping and the launcher's extension, so the same bridge is a remote
        // connector however it is spelled, and on both platforms. The decoder's URL is checked
        // like any other: it vouches for a URL, not for a host free of userinfo.
        if (runner == "npx"
            && RemotePattern.Decode(JsonValue.Object(
                ("command", JsonValue.String("npx")),
                ("args", JsonValue.Array(commandArgs.Select(JsonValue.String))))) is { } remote)
        {
            return UrlOrigin(remote.Url)?.Host ?? RemoteType;
        }
        // The slot where a package runner names the server it fetches: the one place a bare
        // hyphenated word is a package rather than, as likely, a password. It can land on the
        // value of a flag named for a secret, which the first check below drops before it counts.
        var serverSlot = PackageRunners.Contains(runner)
            ? commandArgs.FindIndex(a => !a.StartsWith('-')) : -1;
        var args = commandArgs.Select((arg, index) =>
            index > 0 && IsSecretNamedFlag(commandArgs[index - 1]) ? null : Shown(arg, home, index == serverSlot));
        var tokens = new[] { Launcher(command) }.Concat(args);
        return string.Join(" ", tokens.OfType<string>());
    }

    /// <summary>Launchers whose first positional argument names the package they fetch and run.</summary>
    private static readonly HashSet<string> PackageRunners = ["npx", "uvx", "pipx", "bunx", "pnpx"];

    /// <summary>
    /// A launcher written as a whole command line — <c>npx -y server --token x</c> in one string —
    /// split into arguments the way a shell passes them: its first argument is the launcher and
    /// the rest are arguments ahead of <paramref name="args"/>, each held to the same rule, so a
    /// flag named for a secret at its end guards <c>args[0]</c> as it would any value.
    ///
    /// A command that is a path is not tokenized, since a Windows path holds spaces. It keeps its
    /// launcher — the last component of the path, which runs to the last word holding a separator
    /// — only when the words after its first are plainly more path or plain words: none starts
    /// with <c>-</c> or <c>/</c> or holds <c>:</c> (which covers <c>://</c>), <c>=</c> or a quote.
    /// Otherwise that last component could be the tail of a packed argument, and the launcher is
    /// omitted. The packed words are never shown, but a flag named for a secret at their end,
    /// read with its quotes removed as a shell would pass it, still guards <c>args[0]</c>. A
    /// path-shaped raw secret that passes these checks (<c>C:\x\tool.exe abc\cd.ef</c>) is
    /// accepted: residual 2 in the B1 report.
    /// </summary>
    private static (string, List<string>) SplitCommandLine(string text, IEnumerable<string> args)
    {
        if (!IsExplicitPath(text))
        {
            var tokens = ShellWords(text);
            return (tokens.Count > 0 ? tokens[0] : "", tokens.Skip(1).Concat(args).ToList());
        }
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            return (text, args.ToList());
        }
        var flag = string.Concat(ShellWords(words[^1]));
        var guarded = IsSecretNamedFlag(flag) ? new[] { flag } : [];
        var isPlain = !words.Skip(1).Any(w =>
            w.StartsWith('-') || w.StartsWith('/') || w.Contains(':') || w.Contains('=')
            || w.Contains('"') || w.Contains('\''));
        var pathEnd = Math.Max(Array.FindLastIndex(words, w => w.Contains('/') || w.Contains('\\')), 0);
        return (isPlain ? string.Join(" ", words[..(pathEnd + 1)]) : "", guarded.Concat(args).ToList());
    }

    /// <summary>
    /// <paramref name="text"/> as a shell passes it: whitespace separates arguments, a <c>"…"</c>
    /// or <c>'…'</c> run belongs to the argument it touches and loses its quotes, <c>\"</c> inside
    /// double quotes is a quote, and a backslash anywhere else is itself, as a Windows path needs.
    /// An unterminated quote runs to the end.
    /// </summary>
    private static List<string> ShellWords(string text)
    {
        var words = new List<string>();
        System.Text.StringBuilder? word = null;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else if (open == '"' && c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    word?.Append('"');
                    i++;
                }
                else
                {
                    word?.Append(c);
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                if (word is not null)
                {
                    words.Add(word.ToString());
                }
                word = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                word ??= new System.Text.StringBuilder();
            }
            else
            {
                (word ??= new System.Text.StringBuilder()).Append(c);
            }
        }
        if (word is not null)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>
    /// The launcher, named the way it would be typed, or null when it could be a secret — or when
    /// it holds whitespace, a path with arguments packed into its last component.
    /// </summary>
    private static string? Launcher(string command)
    {
        var name = LauncherName(command);
        return name.Length == 0 || name.Any(char.IsWhiteSpace) || name.Contains('=')
            || CredentialHeuristics.LooksLikeCredential(name) || LooksLikeRandomToken(name) ? null : name;
    }

    /// <summary>
    /// One argument as the target column shows it, or null when it is none of the three shapes
    /// known to be safe: a URL, as its scheme and host; an explicit path, with the home folder
    /// abbreviated; or a named artefact — a package, image, script or module. A bare word, one
    /// with no <c>/</c>, <c>@</c> or <c>.</c>, is an artefact only in the server slot.
    /// </summary>
    private static string? Shown(string arg, string home, bool isServerSlot)
    {
        if (arg.Contains("://", StringComparison.Ordinal))
        {
            return UrlOrigin(arg) is { } origin ? $"{origin.Scheme}://{origin.Host}" : null;
        }
        if (CredentialHeuristics.LooksLikeCredential(arg) || LooksLikeRandomToken(arg))
        {
            return null;
        }
        if (IsExplicitPath(arg))
        {
            // A colon anywhere but a drive letter's is a Windows switch's value, `/p:secret`.
            var colonIsDrive = IsDriveRoot(arg) && !arg[2..].Contains(':');
            if (arg.Contains('=') || (arg.Contains(':') && !colonIsDrive))
            {
                return null;
            }
            var root = TrimmingOneTrailingSeparator(home);
            // Case-insensitive, as both platforms' default file systems are.
            if (root.Length == 0 || !arg.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
            var remainder = arg[root.Length..];
            return remainder.Length == 0 || remainder.StartsWith('/') || remainder.StartsWith('\\') ? "~" + remainder : arg;
        }
        if (arg.Length is 0 or > 100 || !(char.IsAsciiLetterOrDigit(arg[0]) || arg[0] == '@')
            || !arg.All(c => char.IsAsciiLetterOrDigit(c) || "._@/-".Contains(c))
            || !(arg.Any(c => "/.@".Contains(c)) || (isServerSlot && arg.Any(c => "-_".Contains(c)))))
        {
            return null;
        }
        var slash = arg.IndexOf('/');
        if (arg.StartsWith('@') && slash > 1)
        {
            var name = arg[(slash + 1)..];
            if (name.Length > 0 && !name.Contains('/'))
            {
                return "…/" + name;
            }
        }
        return arg;
    }

    /// <summary><paramref name="path"/> without one trailing separator of either kind.</summary>
    private static string TrimmingOneTrailingSeparator(string path) =>
        path.EndsWith('/') || path.EndsWith('\\') ? path[..^1] : path;

    /// <summary>
    /// A random token rather than a name: with its slashes removed, at least 20 characters, with
    /// an upper-case letter, a lower-case letter and a digit, and none of <c>.</c>, <c>-</c> or
    /// <c>_</c>. An AWS secret key has this shape and a <c>/</c>, which <c>LooksLikeCredential</c>
    /// refuses to consider; a real path or package name nearly always has a dot, a hyphen or no digits.
    /// </summary>
    private static bool LooksLikeRandomToken(string text)
    {
        var body = text.Where(c => c != '/' && c != '\\').ToList();
        return body.Count >= 20
            && body.Any(char.IsAsciiLetterUpper) && body.Any(char.IsAsciiLetterLower)
            && body.Any(char.IsAsciiDigit) && !body.Any(c => c is '.' or '-' or '_');
    }

    /// <summary>
    /// A flag without an attached value whose name says the next argument is a secret, whatever
    /// its dashes and case.
    /// </summary>
    private static bool IsSecretNamedFlag(string arg)
    {
        if (!arg.StartsWith('-') || arg.Contains('='))
        {
            return false;
        }
        var name = arg.ToLowerInvariant();
        return SecretNames.Any(name.Contains);
    }

    private static readonly string[] SecretNames = ["token", "key", "secret", "pass", "pwd", "pw", "auth", "credential", "bearer"];

    /// <summary>
    /// Starts with <c>/</c>, <c>~</c>, <c>./</c>, <c>../</c> or a drive root (<c>X:\</c> or <c>X:/</c>).
    /// Internal rather than private because the Publish dialog offers a path row by the same rule.
    /// </summary>
    internal static bool IsExplicitPath(string arg) =>
        PathPrefixes.Any(p => arg.StartsWith(p, StringComparison.Ordinal)) || IsDriveRoot(arg);

    private static readonly string[] PathPrefixes = ["/", "~", "./", "../"];

    /// <summary><c>X:\</c> or <c>X:/</c>.</summary>
    private static bool IsDriveRoot(string arg) =>
        arg.Length >= 3 && char.IsAsciiLetter(arg[0]) && arg[1] == ':' && (arg[2] == '/' || arg[2] == '\\');

    /// <summary>
    /// The scheme and the host of the URL in <paramref name="text"/>, the host with its port when
    /// one is written; null when either is not plainly one. The authority runs from <c>://</c> to
    /// the first <c>/</c>, <c>?</c> or <c>#</c>, and the host is what follows its last <c>@</c>.
    /// What remains must be a bare host and optional port: a password holding an unencoded
    /// <c>/</c>, <c>?</c> or <c>#</c> ends the authority early, and whatever of it is left is
    /// refused rather than shown as the host, as is any URL with an <c>@</c> past its authority,
    /// where the boundary is in doubt. Taken from the text by hand rather than by a URL parser,
    /// because the two platforms' parsers disagree about case and default ports.
    /// </summary>
    private static (string Scheme, string Host)? UrlOrigin(string text)
    {
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }
        var scheme = text[..separator];
        if (scheme.Length is < 2 or > 16 || !char.IsAsciiLetterLower(scheme[0])
            || !scheme.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '+'))
        {
            return null;
        }
        var rest = text[(separator + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        // An `@` after the authority ends means the boundary cannot be trusted: `u:p/w@h` is a
        // password holding a `/`, not a host `u:p` with an `@` in its path.
        if (end >= 0 && rest[end..].Contains('@'))
        {
            return null;
        }
        var authority = end >= 0 ? rest[..end] : rest;
        var host = authority[(authority.LastIndexOf('@') + 1)..];
        var parts = host.Split(':', 2);
        if (parts[0].Length == 0 || !parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            || (parts.Length == 2 && (parts[1].Length is < 1 or > 5 || !parts[1].All(char.IsAsciiDigit))))
        {
            return null;
        }
        return (scheme, host);
    }

    /// <summary>
    /// The last component of a command, splitting on both separators rather than this platform's:
    /// a collection carries connectors authored on either, and a Mac command's launcher is still
    /// worth naming on a PC. Split by hand, because the path APIs on the two platforms disagree
    /// about which separators count. A Windows launcher's <c>.cmd</c>, <c>.exe</c> or <c>.bat</c> is
    /// dropped: <c>npx.cmd</c> is <c>npx</c>, both to name and to recognise.
    /// </summary>
    private static string LauncherName(string command)
    {
        var parts = command.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[^1] : command;
        var isWindowsLauncher = name.Length > 4 && name[^4..].ToLowerInvariant() is ".cmd" or ".exe" or ".bat";
        return isWindowsLauncher ? name[..^4] : name;
    }

    public string DetailLine
    {
        get
        {
            var collection = SelectedCollection;
            if (state.IsSynced(collection))
            {
                return LocatedSource(collection) is { } source
                    ? SyncedDetail(source, SyncStatus(collection))
                    : UnlocatedDetail;
            }
            var count = state.Store.Collections.TryGetValue(collection, out var held) ? held.Mcps.Count : 0;
            var line = LocalDetail(count);
            if (collection == state.ActiveCollection)
            {
                line += ActiveSuffix;
            }
            // Only this machine's binding says where the document goes, so only this machine's
            // window says it publishes. Another machine's publish record is not a fact about this one.
            if (state.CollectionsCache.Published.TryGetValue(collection, out var binding))
            {
                line += " · " + PublishedDetail(binding.Folder);
            }
            return line;
        }
    }

    /// <summary>
    /// The synced document as this machine can name it, or null when it cannot: not synced, no
    /// binding, or a sidecar entry that records neither a path nor a file name. The last of those
    /// takes a hand-edited or foreign collections file — every writer here sets a file name — but
    /// the decoder accepts one, and there is nothing to refresh or to report about a document
    /// nobody can point at.
    /// </summary>
    /// <remarks>
    /// Still its own rule — the detail line has a sentence of its own for an unlocated file — but
    /// the naming is <c>AppState.SourceLocation</c>'s, so the derivation lives in one place.
    /// </remarks>
    private string? LocatedSource(string collection) =>
        state.IsLocated(collection) ? state.SourceLocation(collection) : null;

    /// <summary>What the source is doing, in precedence order: what went wrong outranks what is waiting.</summary>
    private string SyncStatus(string collection)
    {
        if (state.SourceErrors.TryGetValue(collection, out var failure))
        {
            return failure;
        }
        return state.PendingUpdates.ContainsKey(collection) ? UpdateAvailableStatus : UpToDateStatus;
    }

    // MARK: banner strip

    /// <summary>
    /// The banner above the rows, or null. Unlike the flyout's slot, which speaks for whichever
    /// collection has news, this answers only for the collection the window is showing: a strip
    /// over one collection's rows saying something about another one would be a lie.
    /// </summary>
    private CollectionBanner? Banner =>
        state.CollectionBanner is { } banner
            && CollectionBannerPresentation.Collection(banner) == SelectedCollection
            ? banner
            : null;

    public string? BannerText => Banner is { } banner ? CollectionBannerPresentation.Text(banner, state) : null;

    public string? BannerButton => Banner is { } banner ? CollectionBannerPresentation.Button(banner) : null;

    /// <summary>The Mac binds the optional above directly; XAML needs a bool for the strip's visibility.</summary>
    public bool HasBanner => Banner is not null;

    /// <summary>
    /// The strip's button. True says the news is an update, so the view has only to put the Review
    /// dialog in front of the selected collection. False says the view decides by the banner's
    /// kind: a file or folder dialog, handed to <see cref="LocateSource"/> or
    /// <see cref="ChoosePublishFolder"/>, or — for a publish blocked for review — the Publish
    /// dialog, since another folder is no answer to that.
    /// </summary>
    public bool BannerAction() => Banner is CollectionBanner.UpdateAvailable;

    /// <summary>
    /// The Locate button's file, for the collection the window is showing. Null on success, else
    /// the message; also null when the strip is not asking for a file, so a dialog left open past
    /// the news it belonged to cannot point anything anywhere.
    ///
    /// This and <see cref="ChoosePublishFolder"/> return their message rather than
    /// <see cref="Report"/> it, unlike every other verb here: they are the flyout's banner verbs of
    /// the same names, and the two surfaces keep the one shape.
    /// </summary>
    public string? LocateSource(string path) =>
        Banner is CollectionBanner.Locate locate ? state.LocateSource(locate.Collection, path) : null;

    /// <summary>
    /// The Choose Folder button's folder, for the collection the window is showing. Null as
    /// <see cref="LocateSource"/> returns null. Under a publish blocked for review the folder is
    /// refused inside <c>AppState.ChangePublishFolder</c>, which answers with the reason.
    /// </summary>
    public string? ChoosePublishFolder(string folder) => Banner switch
    {
        CollectionBanner.PublishFailed failed => state.ChangePublishFolder(failed.Collection, folder),
        CollectionBanner.PublishBlocked blocked => state.ChangePublishFolder(blocked.Collection, folder),
        _ => null,
    };

    // MARK: control state

    /// <summary>
    /// Where the selection bar's Copy to can send the ticked rows: every collection but the
    /// selected one, which is their source, in the sidebar's order, each marked whether it can
    /// take copies. A synced collection is listed but disabled: its connectors are the author's,
    /// and it has no local write path of its own to copy into.
    /// </summary>
    public IReadOnlyList<CopyDestination> CopyDestinations
    {
        get
        {
            var collection = SelectedCollection;
            return state.CollectionNames
                .Where(name => name != collection)
                .Select(name => new CopyDestination(name, !state.IsSynced(name)))
                .ToList();
        }
    }

    public bool CanRemoveChecked => !state.IsSynced(SelectedCollection) && CheckedNames.Count > 0;

    /// <summary>Refresh reads the bound document, so it needs one this machine can name.</summary>
    public bool CanRefresh => LocatedSource(SelectedCollection) is not null;

    /// <summary>
    /// The last local collection stays, because only a local one takes a new connector, and the
    /// last collection of any kind stays, because the store always has an active one. A synced
    /// collection is never the last local one, so only the second rule reaches it.
    /// </summary>
    public bool CanDelete
    {
        get
        {
            var collection = SelectedCollection;
            return state.IsSynced(collection)
                ? state.CollectionNames.Count > 1
                : state.LocalCollectionNames.Count > 1;
        }
    }

    /// <summary>
    /// Through the rows rather than the tick set, so a tick on a connector that has since
    /// vanished from the collection is dropped instead of exported.
    /// </summary>
    public IReadOnlyList<string> CheckedNames => Rows.Where(r => r.Checked).Select(r => r.Name).ToList();

    // MARK: header pills and menu

    /// <summary>
    /// Marks an exception to the ordinary local collection: Published and Subscribed are
    /// mutually exclusive, since a subscribed collection has an author elsewhere and cannot also
    /// publish. There is no member for the default — an ordinary local collection carries no pill.
    /// </summary>
    public enum Pill
    {
        Active,
        Published,
        Subscribed,
    }

    public const string ActivePill = "Active";
    public const string PublishedPill = "Published";
    public const string SubscribedPill = "Subscribed";

    public static string Title(Pill pill) => pill switch
    {
        Pill.Active => ActivePill,
        Pill.Published => PublishedPill,
        Pill.Subscribed => SubscribedPill,
        _ => throw new ArgumentOutOfRangeException(nameof(pill)),
    };

    /// <summary>
    /// The selected collection's pills, in the header's order: active first, then the one
    /// exception IsSynced and IsPublished cannot both name at once.
    /// </summary>
    public IReadOnlyList<Pill> Pills
    {
        get
        {
            var collection = SelectedCollection;
            var marks = new List<Pill>();
            if (collection == state.ActiveCollection)
            {
                marks.Add(Pill.Active);
            }
            if (state.IsSynced(collection))
            {
                marks.Add(Pill.Subscribed);
            }
            else if (state.IsPublished(collection))
            {
                marks.Add(Pill.Published);
            }
            return marks;
        }
    }

    /// <summary>
    /// The collection's ⋯ menu (spec §4), built here so both platforms show the same list from
    /// the same flags the window's other controls already read. A record hierarchy, the same
    /// shape <see cref="CollectionBanner"/> uses, because two members —
    /// <see cref="MenuEntry.ExportAll"/> and <see cref="MenuEntry.Delete"/> — carry an
    /// <c>Enabled</c> flag the rest do not.
    /// </summary>
    public abstract record MenuEntry
    {
        private MenuEntry()
        {
        }

        public sealed record MakeActive : MenuEntry;

        public sealed record Rename : MenuEntry;

        public sealed record Duplicate : MenuEntry;

        public sealed record StartPublishing : MenuEntry;

        public sealed record PublishingSettings : MenuEntry;

        public sealed record StopPublishing : MenuEntry;

        public sealed record ShowPublishedFile : MenuEntry;

        public sealed record ExportAll(bool Enabled) : MenuEntry;

        public sealed record MakeLocalCopy : MenuEntry;

        public sealed record Refresh : MenuEntry;

        public sealed record ShowSourceFile : MenuEntry;

        public sealed record StopSyncing : MenuEntry;

        public sealed record Delete(bool Enabled) : MenuEntry;

        public sealed record Separator : MenuEntry;
    }

    public const string DuplicateAction = "Duplicate";
    public const string ExportAllAction = "Export All";
    public const string ShowPublishedFileAction = "Show Published File";
    public const string ShowSourceFileAction = "Show Source File";

    public static string Title(MenuEntry entry) => entry switch
    {
        MenuEntry.MakeActive => MakeActiveAction,
        MenuEntry.Rename => RenameAction,
        MenuEntry.Duplicate => DuplicateAction,
        MenuEntry.StartPublishing => PublishButton,
        MenuEntry.PublishingSettings => PublishSettingsButton,
        MenuEntry.StopPublishing => StopPublishingAction,
        MenuEntry.ShowPublishedFile => ShowPublishedFileAction,
        MenuEntry.ExportAll => ExportAllAction,
        MenuEntry.MakeLocalCopy => MakeLocalCopyButton,
        MenuEntry.Refresh => RefreshButton,
        MenuEntry.ShowSourceFile => ShowSourceFileAction,
        MenuEntry.StopSyncing => StopSyncingAction,
        MenuEntry.Delete => DeleteAction,
        MenuEntry.Separator => "",
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };

    /// <summary>
    /// Lists what applies rather than dimming what does not, with one exception: ExportAll is
    /// always present, greyed out on a synced collection, since exporting a read-only mirror is
    /// refused for a reason worth stating rather than a button worth hiding.
    /// </summary>
    public IReadOnlyList<MenuEntry> CollectionMenu
    {
        get
        {
            var collection = SelectedCollection;
            var synced = state.IsSynced(collection);
            var entries = new List<MenuEntry>();
            if (collection != state.ActiveCollection)
            {
                entries.Add(new MenuEntry.MakeActive());
                entries.Add(new MenuEntry.Separator());
            }
            entries.Add(new MenuEntry.Rename());
            entries.Add(synced ? new MenuEntry.MakeLocalCopy() : new MenuEntry.Duplicate());
            entries.Add(new MenuEntry.Separator());
            if (synced)
            {
                if (CanRefresh)
                {
                    entries.Add(new MenuEntry.Refresh());
                }
                if (SourceFilePath is not null)
                {
                    entries.Add(new MenuEntry.ShowSourceFile());
                }
                entries.Add(new MenuEntry.StopSyncing());
            }
            else
            {
                var published = state.IsPublished(collection);
                // Publishing Settings stays beside Stop Publishing: reopening the dialog and pressing
                // Publish again is the only way to change what is shared or to mark a path again.
                entries.Add(published ? new MenuEntry.PublishingSettings() : new MenuEntry.StartPublishing());
                if (published)
                {
                    entries.Add(new MenuEntry.StopPublishing());
                }
                if (PublishedFilePath is not null)
                {
                    entries.Add(new MenuEntry.ShowPublishedFile());
                }
            }
            entries.Add(new MenuEntry.ExportAll(!synced));
            entries.Add(new MenuEntry.Separator());
            entries.Add(new MenuEntry.Delete(CanDelete));
            return entries;
        }
    }

    /// <summary>
    /// Duplicate: the same copy semantics as every other copy in this window — disabled, with
    /// provenance — but of the whole collection, and the selection stays where it was rather than
    /// following the new one the way <see cref="MakeLocalCopy"/> and <see cref="Create"/> do.
    /// </summary>
    public bool Duplicate()
    {
        var collection = SelectedCollection;
        if (state.IsSynced(collection) || dialogs.PromptForName(AppState.NewCollectionTitle, "") is not { } typed)
        {
            LastError = null;
            return false;
        }
        return Report(state.MakeLocalCopyOfCollection(collection, typed));
    }

    /// <summary>
    /// This machine's published document for the selected collection: the folder it writes to,
    /// plus the file name <see cref="PublishedFileName"/> already derives. Null for anything not
    /// published from here, which is what the menu's ShowPublishedFile above tests for.
    /// </summary>
    public string? PublishedFilePath
    {
        get
        {
            var collection = SelectedCollection;
            if (state.CollectionsCache.Published.TryGetValue(collection, out var binding)
                && PublishedFileName(collection) is { } fileName)
            {
                return Path.Combine(binding.Folder, fileName);
            }
            return null;
        }
    }

    /// <summary>
    /// A located synced collection's document — <see cref="LocatedSource"/> again, named for the
    /// menu's ShowSourceFile and the header's own use, both of which want the same null the
    /// detail line already turns into "file not located on this machine".
    /// </summary>
    public string? SourceFilePath => LocatedSource(SelectedCollection);

    // MARK: sidebar and connectors header

    /// <summary>
    /// The sidebar +'s tooltip and accessibility name: its glyph alone does not say that what it
    /// adds is a collection.
    /// </summary>
    public const string AddCollectionTooltip = "Add Collection";
    public const string ImportSubtitle = "Adds copies you own";
    public const string SubscribeSubtitle = "Stays in sync, read-only";
    public const string ConnectorsHeader = "Connectors";
    public const string AddConnectorTooltip = "Add Connector";
    public const string AddConnectorDisabledTooltip = "Additions go in a local collection.";
    /// <summary>The ⋯ button's own tooltip and accessibility label.</summary>
    public const string MoreActionsLabel = "More";

    public bool CanAddConnector => !state.IsSynced(SelectedCollection);

    public string AddConnectorTooltipText => CanAddConnector ? AddConnectorTooltip : AddConnectorDisabledTooltip;

    /// <summary>
    /// The + button on the connector list header: an Add-Remote target in the collection the
    /// window is showing. The Mac's <c>EditTarget.newRemote(in:)</c> takes no style; Windows
    /// always launches a new remote connector through <c>cmd /c npx</c>, the same forced style
    /// <c>EditorWindow.NewRemoteStyle</c> uses.
    /// </summary>
    public EditTarget NewConnectorTarget() => EditTarget.NewRemote(RemoteLaunchStyle.CmdNpx, SelectedCollection);

    // MARK: rows

    /// <summary>A synced collection's rows cannot be exported, so they cannot be ticked either.</summary>
    public void SetChecked(string name, bool on)
    {
        var collection = SelectedCollection;
        if (state.IsSynced(collection))
        {
            return;
        }
        if (checkedCollection != collection)
        {
            checkedNames.Clear();
            checkedCollection = collection;
        }
        if (on)
        {
            checkedNames.Add(name);
        }
        else
        {
            checkedNames.Remove(name);
        }
        RefreshRows();
        Raise(nameof(CanRemoveChecked));
    }

    /// <summary>
    /// The pencil: the same connector in two collections is two windows, so the target carries the
    /// collection this window is showing. The Mac calls this <c>editTarget(for:)</c>; here the
    /// returned type already owns that name.
    /// </summary>
    public EditTarget EditTargetFor(string row)
    {
        var collection = SelectedCollection;
        var entry = state.Store.Collections.TryGetValue(collection, out var held) && held.Mcps.TryGetValue(row, out var found)
            ? found
            : new McpEntry(JsonValue.Object());
        return EditTarget.Existing(row, entry, collection);
    }

    /// <summary>
    /// The names the export sheet writes, in the order the rows show them. <see cref="CheckedNames"/>
    /// today, kept as its own member so that what an export takes is decided here, in one place,
    /// rather than in each window that opens the sheet.
    /// </summary>
    public IReadOnlyList<string> ExportIntentForChecked() => CheckedNames;

    /// <summary>
    /// Copies the ticked connectors into another local collection: they arrive disabled and record
    /// where they came from, so nothing Claude runs changes and nothing is applied. true when they
    /// landed, and the ticks go with them; false when there was nothing to copy, the destination is
    /// not one <see cref="CopyDestinations"/> enables, or the copy failed, with the reason in
    /// <see cref="LastError"/> for the last.
    /// </summary>
    public bool CopyChecked(string collection, IReadOnlyDictionary<string, ImportChoice>? choices = null)
    {
        var names = CheckedNames;
        if (names.Count == 0 || !CopyDestinations.Any(d => d.Name == collection && d.IsEnabled))
        {
            LastError = null;
            return false;
        }
        return Copy(names, collection, choices);
    }

    /// <summary>
    /// Copy to ▸ New Collection: asks for a name, makes an empty local collection, and copies the
    /// ticked connectors into it. The window stays on the collection the rows came from and the
    /// ticks clear, exactly as a copy into an existing collection does. An empty collection rather
    /// than a copy of the active one, because the point is to start one from the ticked rows alone.
    /// true when the copies landed.
    /// </summary>
    public bool CopyCheckedIntoNewCollection()
    {
        var names = CheckedNames;
        if (names.Count == 0 || dialogs.PromptForName(AppState.NewCollectionTitle, "") is not { } typed)
        {
            LastError = null;
            return false;
        }
        if (!Report(state.AddEmptyCollection(typed)))
        {
            return false;
        }
        return Copy(names, MasterStore.CollectionName(typed), null);
    }

    /// <summary>
    /// The ticked connectors whose names the collection already holds: the ones a copy there needs
    /// an answer for. Empty when nothing clashes, so the copy can go straight through.
    /// </summary>
    public IReadOnlyList<string> CheckedNamesClashing(string collection)
    {
        var held = state.Store.Collections.TryGetValue(collection, out var into)
            ? into.Mcps
            : new Dictionary<string, McpEntry>(StringComparer.Ordinal);
        return CheckedNames.Where(held.ContainsKey).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Both copy verbs' shared tail: the copy itself, reported like every other verb here.</summary>
    private bool Copy(IReadOnlyList<string> names, string collection, IReadOnlyDictionary<string, ImportChoice>? choices)
    {
        if (!Report(state.MakeLocalCopy(names, SelectedCollection, collection, choices)))
        {
            return false;
        }
        Uncheck(names);
        return true;
    }

    /// <summary>The ticks on the names cleared in one go, as a copy or a removal leaves them.</summary>
    private void Uncheck(IEnumerable<string> names)
    {
        checkedNames.ExceptWith(names);
        RefreshRows();
        Raise(nameof(CanRemoveChecked));
    }

    /// <summary>
    /// The selection bar's Remove: asks first, names the connector when there is one and the
    /// count when there are more, and always says a copy remains in Backups.
    /// </summary>
    public void RemoveChecked()
    {
        var names = CheckedNames;
        if (names.Count == 0)
        {
            LastError = null;
            return;
        }
        if (!dialogs.Confirm(RemoveCheckedMessage(names), RemoveCheckedInformative, RemoveCheckedButton, destructive: true))
        {
            LastError = null;
            return;
        }
        state.Remove(names, SelectedCollection);
        // Remove(names, collection) persists but does not apply, as its single-name sibling does not.
        if (SelectedCollection == state.ActiveCollection)
        {
            state.ApplyInteractively();
        }
        Uncheck(names);
        LastError = null;
    }

    // MARK: collection actions

    public void Create() =>
        AskNameThenRetarget(AppState.NewCollectionTitle, "", typed => state.CreateCollection(typed));

    public void Rename()
    {
        var collection = SelectedCollection;
        AskNameThenRetarget(AppState.RenameCollectionTitle, collection,
            typed => state.RenameCollection(collection, typed));
    }

    public void Delete()
    {
        var collection = SelectedCollection;
        if (!dialogs.Confirm(AppState.DeleteCollectionMessage(collection), null, AppState.DeleteButton, destructive: true))
        {
            LastError = null;
            return;
        }
        // The store refuses to delete the last local collection, and the last one of any kind.
        // Asked here as well as in the ⋯ menu, so a refusal cannot arrive after the publishing
        // below has already stopped. The store reports it: it refuses before it touches anything,
        // so asking it early is a no-op that still produces the right message.
        if (!CanDelete)
        {
            Report(state.DeleteCollection(collection));
            return;
        }
        if (PublishedFileName(collection) is { } fileName)
        {
            state.StopPublishing(collection, AskAboutPublishedFile(fileName));
        }
        if (Report(state.DeleteCollection(collection)))
        {
            Selected = null;   // back to the active collection
        }
    }

    /// <summary>Stop Publishing: the collection stays, and only the document in the folder is in question.</summary>
    public void StopPublishing()
    {
        var collection = SelectedCollection;
        if (!state.IsPublished(collection))
        {
            LastError = null;
            return;
        }
        // Nothing on this machine writes the document when there is no binding for it, so there is
        // no file here to offer to remove. Nor is there anything to ask while the last write
        // failed: the folder that refused it would refuse the delete too, so the question would be
        // one whose Remove cannot be honoured. The banner's own Stop Publishing says the same by
        // passing false outright.
        // Only a failed write puts the folder out of reach. A publish blocked for review never
        // touched it, so its document can still be removed and the question still stands.
        var failedWrite = state.PublishError is { } error
            && error.Collection == collection && error.Kind == PublishErrorKind.WriteFailed;
        var deleteFile = !failedWrite
            && PublishedFileName(collection) is { } fileName
            && AskAboutPublishedFile(fileName);
        state.StopPublishing(collection, deleteFile);
        LastError = null;
    }

    /// <summary>
    /// Stop Syncing keeps every connector, every filled value and every switch, so there is
    /// nothing to warn about. It still asks, because the question is where the reassurance that
    /// nothing is lost is said; the button no longer carries it.
    /// </summary>
    public void StopSyncing()
    {
        var collection = SelectedCollection;
        if (!state.IsSynced(collection)
            || !dialogs.Confirm(StopSyncingMessage(collection), StopSyncingInformative, StopSyncingAction, destructive: false))
        {
            LastError = null;
            return;
        }
        state.StopSyncing(collection);
        LastError = null;
    }

    public void Refresh()
    {
        var collection = SelectedCollection;
        if (!CanRefresh)
        {
            LastError = null;
            return;
        }
        state.RefreshSource(collection);
        LastError = null;
    }

    /// <summary>The whole synced collection again as a local one the user can edit.</summary>
    public void MakeLocalCopy()
    {
        var collection = SelectedCollection;
        if (!state.IsSynced(collection))
        {
            LastError = null;
            return;
        }
        AskNameThenRetarget(AppState.NewCollectionTitle, collection,
            typed => state.MakeLocalCopyOfCollection(collection, typed));
    }

    public void SwitchTo(string name)
    {
        state.SwitchCollection(name);
        LastError = null;
    }

    // MARK: helpers

    /// <summary>
    /// New Collection, Rename and Make Local Copy: asks for a name, hands it to the verb, and on
    /// success moves the window to the collection the store now keeps under it, rather than
    /// dropping back to the active one. A cancelled prompt clears <see cref="LastError"/>, and a
    /// refusal is reported.
    /// </summary>
    private void AskNameThenRetarget(string title, string initial, Func<string, string?> verb)
    {
        if (dialogs.PromptForName(title, initial) is not { } typed)
        {
            LastError = null;
            return;
        }
        if (Report(verb(typed)))
        {
            Retarget(MasterStore.CollectionName(typed));
        }
    }

    /// <summary>The document this machine writes for the collection, or null when nothing here publishes it.</summary>
    private string? PublishedFileName(string collection) =>
        state.CollectionsCache.Published.ContainsKey(collection)
        && state.CollectionsFile.Collections.TryGetValue(collection, out var entry)
        && entry.Publish is { } record
            ? CollectionDocument.FileName(record.Slug)
            : null;

    /// <summary>Default no: the view's default button is Keep, and this model only records the answer.</summary>
    private bool AskAboutPublishedFile(string fileName) =>
        dialogs.Confirm(DeletePublishedFileQuestion(fileName), null, RemoveFileButton, KeepFileButton, destructive: false);

    private bool Report(string? error)
    {
        LastError = error;
        return error is null;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (RelevantProperties.Any(name => Affects(e, name)))
        {
            ForgetUnresolvedSelection();
            RefreshPanes();
            RaiseSelectionDependents();
        }
    }

    public void Dispose() => state.PropertyChanged -= OnStateChanged;
}
