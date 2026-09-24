using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ConnectorControl.Core.State;

/// <summary>
/// The editor window without the pixels: every field, switch rule, validation string, and the
/// save flow. The loss warning and the save-conflict question both go through
/// <see cref="IDialogs.Confirm"/>; the Mac's loss warning is published state its view binds to
/// as a sheet instead, because WPF has no sheet.
///
/// Mirror: Sources/ConnectorControlState/EditorModel.swift
/// </summary>
public sealed class EditorModel : ObservableObject, IDisposable
{
    public const string NotValidJson = "Not valid JSON — check for a stray brace, missing comma, or unquoted value.";
    public const string JsonTip = "Tip: paste a README snippet or an mcpServers stanza — a wrapper or a bare \"name\": {…} entry is unwrapped automatically, and the name filled in.";
    public const string UrlHint = "Enter a valid http(s) URL, e.g. https://example.com/mcp";
    public const string RemoteFooter = "Runs via npx mcp-remote — managed for you.";
    public const string AutomaticCaption = "Uses the server's OAuth (a browser window opens on first use), or no auth if the server is open.";
    public const string BearerCaption = "Sent as Authorization: Bearer …";
    /// <summary>
    /// mcp-remote reads --static-oauth-client-info literally (or from an @file); it has no env-var
    /// indirection for it the way the header flags do, so unlike the token and header fields this
    /// value ends up on the process command line.
    /// </summary>
    public const string OAuthSecretCaption = "Passed to mcp-remote on its command line, which other programs running on this PC can read.";
    public const string InvalidUrlError = "Server URL must be a valid http(s) URL.";
    /// <summary>Under the cmd /c launcher cmd.exe re-parses every argument. The collection renderer says the same thing, so the wording lives beside the check.</summary>
    public const string CmdUnsafeSuffix = RemotePattern.CmdUnsafeSuffix;
    public static string CmdUnsafeError(string field) => field + CmdUnsafeSuffix;
    public const string CmdPercentCaution = "This URL has more than one %, which cmd.exe can expand as a variable. If the connector fails to start, check its JSON view.";
    public const string BearerTokenError = "Enter a bearer token.";
    public const string HeaderNameError = "Enter a header name.";
    public const string HeaderValueError = "Enter a header value.";
    public const string ClientIdError = "Enter a client ID.";
    public const string CommandError = "Command must not be empty.";
    public const string EnvNamelessError = "An environment variable value is missing its name.";
    public const string LossWarningPrefix = "Switching to Form view can’t fully represent this configuration. These elements would be lost or altered:\n";
    public const string SwitchAnywayButton = "Switch Anyway";
    public const string StayInJsonButton = "Stay in JSON";
    public const string SaveAnywayButton = "Save Anyway";
    public const string AddArgumentTitle = "＋ Add argument";
    public const string AddVariableTitle = "＋ Add variable";
    public const string ChangedOutsideDetail = "Saving will overwrite that change with this editor's version.";
    public const string RemovedOutsideDetail = "Saving will add it back.";
    public const string WhatCanIChange = "What can I change?";
    public const string WhatCanIChangeAnswer = "Fill in the highlighted values and switch it on or off. Everything else follows the source; make a local copy to change it.";
    public const string NeedsValue = "needs your value";
    public const string NeedsPath = "needs your path";

    public static string LockedFieldsNote(string collection) => $"Synced from {collection} · read-only";

    public static string PublishedNote(string folder) =>
        $"Published to {folder} — saving updates the file your team reads. Secrets stay here.";

    public static string ImportedNote(string collection, string date) =>
        $"Imported from “{collection}” on {date}. Edits stay here.";

    public static string PropagateLabel(string collections, string connector) =>
        $"Also apply this change to {collections}, which has an identical {connector}";

    public static string DuplicateEnvError(string name) => $"Duplicate environment variable name: {name}";
    public static string ChangedOutsideMessage(string name) => $"“{name}” changed outside this editor.";
    public static string RemovedOutsideMessage(string name) => $"“{name}” was removed outside this editor.";

    /// <summary>The Mac's static and an instance property of the same name can coexist there; C# forbids that, so the instance property below calls this.</summary>
    public static string AdditionalTitleFor(int count, IEnumerable<string> keys) =>
        $"{count} field(s) not editable here: {string.Join(", ", keys)} — switch to JSON to edit";

    /// <summary>
    /// The grey line above the fields, and what it means for what the window may change.
    ///
    /// Mirror: Sources/ConnectorControlState/EditorModel.swift
    /// </summary>
    public abstract record HeaderState
    {
        private HeaderState()
        {
        }

        /// <summary>An ordinary connector in an ordinary local collection: today's editor, unchanged.</summary>
        public sealed record None : HeaderState;

        /// <summary>Somebody else's collection: everything but the placeholders belongs to its author.</summary>
        public sealed record Synced(string Collection) : HeaderState;

        /// <summary>A local collection whose document this machine writes: saving rewrites it.</summary>
        public sealed record Published(string Folder) : HeaderState;

        /// <summary>A copy taken from another collection, which has gone its own way since.</summary>
        public sealed record Imported(string From, string Date) : HeaderState;

        /// <summary>
        /// Which state this is, as three bools. XAML cannot pattern match, and a string in a
        /// converter parameter collapses a header on a typo with nothing to catch it; the auth
        /// kinds carry the same trio one screen below for the same reason. The Mac needs none of
        /// these — SwiftUI switches on the enum itself.
        /// </summary>
        public bool IsSynced => this is Synced;

        public bool IsPublished => this is Published;

        public bool IsImported => this is Imported;
    }

    /// <summary>The picker's order, as an array so <see cref="AuthKindIndex"/> can search it without allocating.</summary>
    private static readonly RemoteAuthKind[] AuthKindOrder =
        [RemoteAuthKind.Automatic, RemoteAuthKind.Bearer, RemoteAuthKind.Header, RemoteAuthKind.OAuthClient];

    public static readonly IReadOnlyList<RemoteAuthKind> AuthKinds = AuthKindOrder;

    public static readonly IReadOnlyList<string> AuthKindTitles = AuthKinds.Select(k => k.Title()).ToList();

    private readonly AppState state;
    private readonly IDialogs dialogs;
    /// <summary>
    /// True only for a brand-new connector still showing the remote template's placeholder
    /// command/args. Set at open; consumed by the first discard (below) or by adopting an edited
    /// JSON view into the form, since from then on the command/args are the user's own, not a
    /// re-derivable property of the current fields — a later Type toggle must not wipe what they
    /// typed. An unchanged JSON round trip (open JSON, switch straight back) leaves it set.
    /// </summary>
    private bool isUntouchedTemplate;
    private bool propagate;

    private EditView view;
    private string name;
    private bool isRemote;
    private string remoteUrl;
    private RemoteAuthKind authKind = RemoteAuthKind.Automatic;
    private string bearerToken = "";
    private string headerName = "";
    private string headerValue = "";
    private string oauthClientId = "";
    private string oauthClientSecret = "";
    private string oauthScopes = "";
    private string remotePackage = RemotePattern.DefaultPackage;
    private IReadOnlyList<string> remoteExtraArgs = [];
    private IReadOnlyDictionary<string, string> remotePassthroughEnv = new Dictionary<string, string>(StringComparer.Ordinal);
    private RemoteLaunchStyle remoteLaunchStyle;
    private string command;
    private IReadOnlyDictionary<string, JsonValue> additional;
    private string jsonText;
    /// <summary>`jsonText` recovered once per edit, in its setter below; ValidateJson and ComputeRequiredTool read this instead of recovering it again.</summary>
    private PasteResult? recoveredJson;
    private string? jsonError;
    /// <summary>
    /// What the connector was asking this machine for when the window opened; see
    /// <see cref="AsksFor"/> for why it is fixed rather than re-read.
    /// </summary>
    private HashSet<EnvRow> askedEnvRows;
    private HashSet<ArgRow> askedArgs;
    /// <summary>
    /// Whether the form was read-only when the snapshot above was taken. One transition retakes
    /// it: a collection that stops syncing turns every field into an ordinary editable one, and
    /// a field still rendered as the unmasked placeholder control would be a secret in the clear
    /// in a form that no longer locks anything.
    /// </summary>
    private bool snapshotWasReadOnly;
    /// <summary>
    /// Where each argument sat in the config the window opened on, by row. The publish record
    /// keys a path mark by its pointer, so a hint belongs to a position in the document that was
    /// published, not to whatever position the row holds now — and a published collection's
    /// editor is fully editable, so rows move. Fixed at open and never retaken, unlike the
    /// asked-for snapshot, on the assumption that the published document does not change under
    /// the window. A republish from the Collections window while this editor is open breaks that
    /// assumption: the hints then describe the document as it was at open.
    /// </summary>
    private Dictionary<ArgRow, int> openArgIndexByRow = [];
    /// <summary>
    /// The arguments the window opened on, which the publish record's path marks are placed
    /// against. Fixed with <see cref="openArgIndexByRow"/>, for the same reason.
    /// </summary>
    private readonly List<string> openedArgs = [];
    /// <summary>
    /// Whether every argument row still traces back to the config the window opened on. A JSON
    /// edit that changed the arguments rebuilds the rows and carries their open positions by
    /// position alone (<see cref="CarriedRecords"/>), which is a guess a path mark must not bet a
    /// path on: from then on a save leaves the marks for publishing to place by their values.
    /// </summary>
    private bool argRowsFollowOpen = true;
    private string? validationError;
    private Tool? requiredTool;
    private bool suppressToolEvaluation;

    public EditorModel(AppState state, EditTarget target, IDialogs dialogs, RemoteLaunchStyle newRemoteStyle)
    {
        this.state = state;
        this.dialogs = dialogs;
        Target = target;
        isUntouchedTemplate = target.IsNew && target.ForcesRemote;
        PropagateTargets = TwinsOf(state, target);
        name = target.Name;
        view = target.Entry.LastEditView;
        var config = target.Entry.Config;
        // Placeholders: every field needs a value before Load (an instance method) can run; it
        // overwrites all of these.
        remoteUrl = "";
        command = "";
        Args = [];
        EnvRows = [];
        additional = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        jsonText = config.EditorText();
        recoveredJson = PasteRecovery.Recover(jsonText);
        remoteLaunchStyle = newRemoteStyle;
        Load(config);
        // On open, a cached status shows its note at once; an
        // unknown one is probed now. Later changes go through EvaluateRequiredTool.
        askedEnvRows = [];
        askedArgs = [];
        TakeAskedSnapshot();
        for (var i = 0; i < Args.Count; i++)
        {
            openArgIndexByRow[Args[i]] = i;
            openedArgs.Add(Args[i].Value);
        }
        state.PropertyChanged += OnStateChanged;
        Args.CollectionChanged += OnArgsChanged;
        requiredTool = ComputeRequiredTool();
        if (requiredTool is { } initial && !state.ToolStatuses.ContainsKey(initial))
        {
            _ = state.RefreshToolsAsync([initial]);
        }
    }

    public EditTarget Target { get; }

    public string WindowTitle => Target.WindowTitle;

    public event Action? CloseRequested;

    /// <summary>Raised when a fresh env row wants keyboard focus on its name field.</summary>
    public event Action<EnvRow>? FocusEnvRowRequested;

    // MARK: view

    /// <summary>
    /// The two segmented buttons' binding: a set is a request; a refused switch snaps back, since
    /// RequestView raises this property whether or not it actually switched.
    /// </summary>
    public EditView View
    {
        get => view;
        set => RequestView(value);
    }

    private void SetView(EditView value)
    {
        if (Set(ref view, value, nameof(View)))
        {
            Raise(nameof(CanSave));
            EvaluateRequiredTool();
        }
    }

    // MARK: fields

    public string Name { get => name; set => Set(ref name, value); }

    public bool ShowTypePicker => Target.IsNew;

    /// <summary>The Type picker (new targets only). Switching to Local discards the remote template's bridge invocation.</summary>
    public bool IsRemote
    {
        get => isRemote;
        set
        {
            if (!Set(ref isRemote, value))
            {
                Raise(nameof(IsLocal));
                return;
            }
            Raise(nameof(IsLocal));
            Raise(nameof(CanSave));
            if (!isRemote && View == EditView.Form && isUntouchedTemplate)
            {
                // Discard the remote template's bridge invocation — a local server has nothing
                // to do with mcp-remote. The template is consumed by this one discard; from here
                // on the fields are the user's own local form, so a later switch back and forth
                // must not re-derive and repeat it.
                Command = "npx";
                Args.Clear();
                Args.Add(new ArgRow("-y"));
                Args.Add(new ArgRow(""));
                isUntouchedTemplate = false;
            }
            EvaluateRequiredTool();
        }
    }

    public bool IsLocal { get => !isRemote; set => IsRemote = !value; }

    public string RemoteUrl
    {
        get => remoteUrl;
        set
        {
            if (Set(ref remoteUrl, value))
            {
                Raise(nameof(RemoteUrlValid));
                Raise(nameof(RemoteUrlCmdSafe));
                Raise(nameof(ShowUrlHint));
                Raise(nameof(UrlCaution));
                Raise(nameof(ShowUrlCaution));
                Raise(nameof(CanSave));
            }
        }
    }

    /// <summary>Basic URL syntax check for the remote form: http(s) scheme and a host.</summary>
    public bool RemoteUrlValid => RemotePattern.IsValidHttpUrl(remoteUrl);

    public bool ShowUrlHint => remoteUrl.Length > 0 && !RemoteUrlValid;

    /// <summary>
    /// True unless this connector is launched through <c>cmd /c</c> and the URL carries a character
    /// cmd.exe would act on (<see cref="RemotePattern.CmdUnsafeCharacters"/> or whitespace).
    /// </summary>
    public bool RemoteUrlCmdSafe => remoteLaunchStyle != RemoteLaunchStyle.CmdNpx || RemotePattern.CmdUnsafeCharacter(remoteUrl) is null;

    /// <summary>Under the URL field: the hard reason Save is disabled, a soft caution about % expansion, or null.</summary>
    public string? UrlCaution
    {
        get
        {
            if (remoteUrl.Length == 0 || !RemoteUrlValid)
            {
                return null;
            }
            if (!RemoteUrlCmdSafe)
            {
                return CmdUnsafeError(Label(RemoteField.Url));
            }
            if (remoteLaunchStyle == RemoteLaunchStyle.CmdNpx && RemotePattern.HasCmdExpansionRisk(remoteUrl))
            {
                return CmdPercentCaution;
            }
            return null;
        }
    }

    public bool ShowUrlCaution => UrlCaution is not null;

    public RemoteAuthKind AuthKind
    {
        get => authKind;
        private set
        {
            if (Set(ref authKind, value))
            {
                Raise(nameof(AuthKindIndex));
                Raise(nameof(IsAutomatic));
                Raise(nameof(IsBearer));
                Raise(nameof(IsHeader));
                Raise(nameof(IsOAuth));
            }
        }
    }

    /// <summary>ComboBox binding over <see cref="AuthKindTitles"/>.</summary>
    public int AuthKindIndex
    {
        get => Array.IndexOf(AuthKindOrder, authKind);
        set
        {
            if (value >= 0 && value < AuthKinds.Count)
            {
                AuthKind = AuthKinds[value];
            }
            else
            {
                Raise(nameof(AuthKindIndex));   // a ComboBox cleared to -1 snaps back to the current kind
            }
        }
    }

    public bool IsAutomatic => authKind == RemoteAuthKind.Automatic;
    public bool IsBearer => authKind == RemoteAuthKind.Bearer;
    public bool IsHeader => authKind == RemoteAuthKind.Header;
    public bool IsOAuth => authKind == RemoteAuthKind.OAuthClient;

    /// <summary>
    /// The three secret fields raise what is read off their text as well as the text itself: the
    /// caution ring and the author's hint under a field follow whether its value is still a
    /// placeholder, so the two have to move together. Whether the field is locked does not: that
    /// is the asked-for snapshot taken when the window opened, so typing over the marker never
    /// locks the field again. The Mac needs none of this — there these are <c>@Published</c>, and
    /// a change to one republishes the whole object.
    /// </summary>
    public string BearerToken
    {
        get => bearerToken;
        set
        {
            if (Set(ref bearerToken, value))
            {
                Raise(nameof(BearerTokenIsPlaceholder));
                Raise(nameof(BearerTokenHint));
            }
        }
    }

    public string HeaderName { get => headerName; set => Set(ref headerName, value); }

    public string HeaderValue
    {
        get => headerValue;
        set
        {
            if (Set(ref headerValue, value))
            {
                Raise(nameof(HeaderValueIsPlaceholder));
                Raise(nameof(HeaderValueHint));
            }
        }
    }

    public string OAuthClientId { get => oauthClientId; set => Set(ref oauthClientId, value); }

    public string OAuthClientSecret
    {
        get => oauthClientSecret;
        set
        {
            if (Set(ref oauthClientSecret, value))
            {
                Raise(nameof(ClientSecretIsPlaceholder));
                Raise(nameof(ClientSecretHint));
            }
        }
    }

    public string OAuthScopes { get => oauthScopes; set => Set(ref oauthScopes, value); }

    public string Command
    {
        get => command;
        set
        {
            if (Set(ref command, value))
            {
                EvaluateRequiredTool();
            }
        }
    }

    public ObservableCollection<ArgRow> Args { get; }

    public ObservableCollection<EnvRow> EnvRows { get; }

    public bool HasEnvRows => EnvRows.Count > 0;

    public bool HasAdditional => additional.Count > 0;

    public string AdditionalTitle => AdditionalTitleFor(additional.Count, additional.Keys.Order(StringComparer.Ordinal));

    public string AdditionalPreview => JsonValue.Object(additional).EditorText();

    public string JsonText
    {
        get => jsonText;
        set
        {
            if (Set(ref jsonText, value))
            {
                recoveredJson = PasteRecovery.Recover(jsonText);
                ValidateJson();
                EvaluateRequiredTool();
            }
        }
    }

    public string? JsonError
    {
        get => jsonError;
        private set
        {
            if (Set(ref jsonError, value))
            {
                Raise(nameof(HasJsonError));
                Raise(nameof(CanSave));
                // The paste tip offers what a JSON view with an error in it cannot do.
                Raise(nameof(ShowJsonTip));
            }
        }
    }

    public bool HasJsonError => jsonError is not null;

    public string? ValidationError
    {
        get => validationError;
        private set
        {
            if (Set(ref validationError, value))
            {
                Raise(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => validationError is not null;

    /// <summary>
    /// Save is disabled with a JSON error, or in the remote form without a valid, cmd-safe URL.
    /// A read-only window saves only the placeholders, none of which can put it in either state,
    /// so its Save stays enabled.
    /// </summary>
    public bool CanSave => IsReadOnly || !((view == EditView.Json && jsonError is not null) || (view == EditView.Form && isRemote && !(RemoteUrlValid && RemoteUrlCmdSafe)));

    // MARK: collection

    /// <summary>The collection this window edits.</summary>
    public string CollectionName => Target.Collection;

    /// <summary>
    /// A synced collection's connectors belong to the author of its document. Everything but the
    /// values the document asks this machine for is locked, and a save may move only those.
    /// </summary>
    public bool IsReadOnly => state.IsSynced(CollectionName);

    /// <summary>
    /// EditorModel.swift calls this <c>headerState</c>: C# forbids a property and a nested type
    /// of the same name on one class, and the type is the one both sides spell HeaderState.
    /// </summary>
    public HeaderState Header
    {
        get
        {
            var collection = CollectionName;
            if (state.IsSynced(collection))
            {
                return new HeaderState.Synced(collection);
            }
            // Publishing is per machine: a collection somebody else publishes says nothing here,
            // because this machine writes no file for it.
            if (state.CollectionsCache.Published.TryGetValue(collection, out var binding))
            {
                return new HeaderState.Published(binding.Folder);
            }
            if (state.CollectionsFile.Collections.TryGetValue(collection, out var entry)
                && entry.Provenance.TryGetValue(Target.Name, out var provenance))
            {
                return new HeaderState.Imported(provenance.From, provenance.Date);
            }
            return new HeaderState.None();
        }
    }

    /// <summary>The one grey line the header shows, or null for an ordinary local connector.</summary>
    public string? HeaderNote => Header switch
    {
        HeaderState.Synced synced => LockedFieldsNote(synced.Collection),
        HeaderState.Published published => PublishedNote(published.Folder),
        HeaderState.Imported imported => ImportedNote(imported.From, imported.Date),
        _ => null,
    };

    public bool HasHeaderNote => HeaderNote is not null;

    /// <summary>The paste tip offers something a read-only JSON view cannot do.</summary>
    public bool ShowJsonTip => !IsReadOnly && jsonError is null;

    /// <summary>
    /// The other local collections that held a byte-identical copy of this connector when the
    /// window opened. Fixed there rather than re-derived: the checkbox names them, and the save
    /// that follows must write to the collections the user was shown, not to whatever matches by
    /// the time they click.
    /// </summary>
    public IReadOnlyList<string> PropagateTargets { get; }

    public bool ShowPropagate => PropagateTargets.Count > 0;

    public string PropagateMessage => PropagateLabel(string.Join(", ", PropagateTargets), Target.Name);

    /// <summary>The propagate checkbox: off unless the user ticks it.</summary>
    public bool Propagate { get => propagate; set => Set(ref propagate, value); }

    private static IReadOnlyList<string> TwinsOf(AppState state, EditTarget target)
    {
        var collection = target.Collection;
        // A connector that does not exist yet has no twins, and a synced collection's copy is its
        // author's — neither offers the checkbox.
        if (target.IsNew || state.KindOf(collection) != CollectionKind.Local)
        {
            return [];
        }
        return state.LocalCollectionNames
            .Where(other => other != collection
                && state.Store.Collections.TryGetValue(other, out var held)
                && held.Mcps.TryGetValue(target.Name, out var twin)
                && twin.Config == target.Entry.Config)
            .ToList();
    }

    // MARK: placeholders

    /// <summary>What the last Apply recorded this connector asking this machine for, by marker name.</summary>
    private IReadOnlyDictionary<string, CollectionsFile.Need> CollectionNeeds => state.Needs(Target.Name, CollectionName);

    /// <summary>
    /// The hint for the first marker still standing in <paramref name="text"/>, if the document
    /// supplied one. A filled field carries no marker, so it asks for nothing and says nothing.
    /// </summary>
    private string? Hint(string text)
    {
        var needs = CollectionNeeds;
        foreach (var marker in Placeholder.NamesIn(text))
        {
            if (needs.TryGetValue(marker, out var need) && need.Hint is { } hint)
            {
                return hint;
            }
        }
        return null;
    }

    /// <summary>
    /// Takes the row itself where EditorModel.swift takes an id: this EnvRow is an ObservableObject
    /// the view holds on to, so its live value is right here, while Swift's is a struct in an array
    /// that has to be looked up by id.
    /// </summary>
    public bool IsPlaceholder(EnvRow row) => Placeholder.ContainsMarker(row.Value);

    /// <summary>The row's hint, live; see IsPlaceholder for why this side takes the row.</summary>
    public string? PlaceholderHint(EnvRow row) => Hint(row.Value);

    /// <summary>Argument indexes still carrying a marker: locked in a synced collection, but live.</summary>
    public IReadOnlySet<int> ArgsWithPlaceholders =>
        Args.Select((row, index) => (row, index))
            .Where(pair => Placeholder.ContainsMarker(pair.row.Value))
            .Select(pair => pair.index)
            .ToHashSet();

    /// <summary>EditorModel.swift's <c>placeholderHint(arg:)</c>; C# has no argument labels to tell the two apart.</summary>
    public string? PlaceholderHintForArg(int index) =>
        index >= 0 && index < Args.Count ? Hint(Args[index].Value) : null;

    public bool BearerTokenIsPlaceholder => Placeholder.ContainsMarker(bearerToken);

    public bool HeaderValueIsPlaceholder => Placeholder.ContainsMarker(headerValue);

    public bool ClientSecretIsPlaceholder => Placeholder.ContainsMarker(oauthClientSecret);

    /// <summary>
    /// Whether this row was asking for a value when the window opened, which is what unlocks it
    /// in a read-only form. Stays true after the value is filled in, unlike
    /// <see cref="IsPlaceholder"/>: a field being typed into must not turn into a locked one
    /// between two keystrokes. Keyed by the row object, so inserting a row above it changes
    /// nothing — by the row object here, where the Mac's rows are structs and carry a UUID for
    /// the same purpose. The Mac also takes an id rather than the row, as <see cref="IsPlaceholder"/>
    /// explains.
    /// </summary>
    public bool AsksFor(EnvRow row) => askedEnvRows.Contains(row);

    /// <summary>
    /// The same question for an argument. Takes an index because that is what a view has, and
    /// resolves it through the row's identity so a row inserted above does not move the answer.
    /// </summary>
    public bool AsksForArg(int index) =>
        index >= 0 && index < Args.Count && askedArgs.Contains(Args[index]);

    public bool AsksForBearerToken { get; private set; }

    public bool AsksForHeaderValue { get; private set; }

    public bool AsksForClientSecret { get; private set; }

    // MARK: published hints

    /// <summary>
    /// The author's hints for the values publishing strips, by environment variable name. Empty
    /// unless this machine is the one publishing the collection: another machine's record says
    /// what it strips, not what this editor is looking at.
    /// </summary>
    private IReadOnlyDictionary<string, string> PublishedEnvHints
    {
        get
        {
            if (!state.CollectionsCache.Published.ContainsKey(CollectionName))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return state.CollectionsFile.Collections.GetValueOrDefault(CollectionName)?.Publish?.Intent
                       .Hints.GetValueOrDefault(Target.Name)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The author's hint beside a value publishing strips. Distinct from
    /// <see cref="PlaceholderHint"/>, which reads the synced sidecar's needs and is therefore
    /// always null in the published state.
    /// </summary>
    public string? PublishedHint(EnvRow row) => PublishedEnvHints.GetValueOrDefault(row.Name);

    /// <summary>
    /// An argument's hint, which the record keys by where the marker sits rather than by name.
    /// Resolved through the row's identity, as <see cref="AsksForArg"/> is: a published
    /// collection's editor adds and removes arguments freely, and a hint read by today's
    /// position would sit beside whichever row happened to slide into it. Null for a row added
    /// since the window opened, which the published document has never described.
    /// </summary>
    public string? PublishedHintForArg(int index)
    {
        if (!state.CollectionsCache.Published.ContainsKey(CollectionName) || index < 0 || index >= Args.Count
            || !openArgIndexByRow.TryGetValue(Args[index], out var published))
        {
            return null;
        }
        // Placed on the opened arguments the way the exporter places it, so a mark that moved
        // before the window opened still shows its hint beside the argument it stands for.
        var marks = state.CollectionsFile.Collections.GetValueOrDefault(CollectionName)?.Publish?.Intent
            .PathMarks.GetValueOrDefault(Target.Name);
        return marks is null ? null : PublishIntent.PlacePathMarks(marks, openedArgs).Placed.GetValueOrDefault(published)?.Hint;
    }

    /// <summary>
    /// Whether this connector has any author's hint to show at all, so the view can leave the
    /// column out rather than reserve space for nothing.
    /// </summary>
    public bool HasPublishedHints
    {
        get
        {
            if (!state.CollectionsCache.Published.ContainsKey(CollectionName))
            {
                return false;
            }
            if (PublishedEnvHints.Count > 0)
            {
                return true;
            }
            var marks = state.CollectionsFile.Collections.GetValueOrDefault(CollectionName)?.Publish?.Intent
                .PathMarks.GetValueOrDefault(Target.Name);
            return marks is not null && marks.Values.Any(m => m.Hint is not null);
        }
    }

    // MARK: live placeholder flags

    public string? BearerTokenHint => Hint(bearerToken);

    public string? HeaderValueHint => Hint(headerValue);

    public string? ClientSecretHint => Hint(oauthClientSecret);

    // MARK: tool note

    /// <summary>
    /// The launcher this connector needs: npx in the remote form, the Command field (through one
    /// <c>cmd /c</c>) in the local form, the parsed config in the JSON view; null for none, a
    /// path, or unparseable JSON.
    /// </summary>
    public Tool? RequiredTool => requiredTool;

    /// <summary>Null while the tool is unknown (not probed yet) or found. Never blocks Save.</summary>
    public ToolNote? ToolNote =>
        requiredTool is { } tool && state.ToolStatuses.TryGetValue(tool, out var status) ? Core.ToolNote.Make(tool, status) : null;

    public bool HasToolNote => ToolNote is not null;

    private Tool? ComputeRequiredTool()
    {
        if (view == EditView.Json)
        {
            return recoveredJson is { } recovered ? ToolRequirement.RequiredTool(recovered.Config) : null;
        }
        return isRemote ? Tool.Npx : ToolRequirement.RequiredTool(command, Args.Select(a => a.Value).ToList());
    }

    /// <summary>A change to a different tool re-probes it even if cached — the user may have just installed it.</summary>
    private void EvaluateRequiredTool()
    {
        if (suppressToolEvaluation)
        {
            return;
        }
        var tool = ComputeRequiredTool();
        if (tool == requiredTool)
        {
            return;
        }
        requiredTool = tool;
        Raise(nameof(RequiredTool));
        Raise(nameof(ToolNote));
        Raise(nameof(HasToolNote));
        if (tool is { } changed)
        {
            _ = state.RefreshToolsAsync([changed]);
        }
    }

    private void OnArgsChanged(object? sender, NotifyCollectionChangedEventArgs e) => EvaluateRequiredTool();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Affects(e, nameof(AppState.ToolStatuses)))
        {
            Raise(nameof(ToolNote));
            Raise(nameof(HasToolNote));
        }
        // Everything the collection decides: its kind, the grey line above the fields, what the
        // footer offers, and whether the author left a hint beside a stripped value. The window
        // used to re-seat its DataContext to pick these up, which regenerated every row and took
        // the caret with it. The Mac needs none of this: its whole object republishes.
        if (Affects(e, nameof(AppState.CollectionsFile)) || Affects(e, nameof(AppState.CollectionsCache))
            || Affects(e, nameof(AppState.Store)))
        {
            // The retake comes first, so the record is already the new one by the time anything
            // hears that the form unlocked. A binding that transfers inline off IsReadOnly would
            // otherwise read the record this is about to replace; WPF's DataBind priority hides
            // that today, which is not a thing to depend on.
            var retook = RetakeSnapshotIfUnlocked();
            Raise(nameof(IsReadOnly));
            Raise(nameof(Header));
            Raise(nameof(HeaderNote));
            Raise(nameof(HasHeaderNote));
            Raise(nameof(CanSave));
            Raise(nameof(ShowJsonTip));
            Raise(nameof(HasPublishedHints));
            if (retook)
            {
                Raise(nameof(AsksForBearerToken));
                Raise(nameof(AsksForHeaderValue));
                Raise(nameof(AsksForClientSecret));
            }
        }
    }

    /// <summary>What every field is asking for right now, recorded as the answer the locks will use.</summary>
    private void TakeAskedSnapshot()
    {
        askedEnvRows = EnvRows.Where(r => Placeholder.ContainsMarker(r.Value)).ToHashSet();
        askedArgs = Args.Where(r => Placeholder.ContainsMarker(r.Value)).ToHashSet();
        AsksForBearerToken = Placeholder.ContainsMarker(bearerToken);
        AsksForHeaderValue = Placeholder.ContainsMarker(headerValue);
        AsksForClientSecret = Placeholder.ContainsMarker(oauthClientSecret);
        snapshotWasReadOnly = IsReadOnly;
    }

    /// <summary>
    /// Stop Syncing under an open window: the form stops locking anything, so the snapshot taken
    /// against a locked form has nothing left to protect and a field the user has since filled
    /// must go back to being an ordinary masked secret. Taken from the values as they stand, so
    /// a field still holding a marker keeps asking. The values are read as they stand now, not
    /// from the config the window opened on. The view gates its control choice on
    /// <see cref="IsReadOnly"/> too, so this only decides which fields stay live inside a form
    /// that still locks the rest. True when it retook.
    /// </summary>
    private bool RetakeSnapshotIfUnlocked()
    {
        if (!snapshotWasReadOnly || IsReadOnly)
        {
            return false;
        }
        TakeAskedSnapshot();
        return true;
    }

    /// <summary>Stops listening to AppState; the window calls this from Closed.</summary>
    public void Dispose()
    {
        state.PropertyChanged -= OnStateChanged;
        Args.CollectionChanged -= OnArgsChanged;
    }

    // MARK: list editing

    public void AddArg() => Args.Add(new ArgRow(""));

    public void RemoveArg(ArgRow row) => Args.Remove(row);

    /// <summary>A fresh row's value is shown in clear — the user is typing it, not inspecting a stored secret.</summary>
    public void AddEnvRow()
    {
        var row = new EnvRow("", "") { Revealed = true };
        EnvRows.Add(row);
        Raise(nameof(HasEnvRows));
        FocusEnvRowRequested?.Invoke(row);
    }

    public void RemoveEnvRow(EnvRow row)
    {
        EnvRows.Remove(row);
        Raise(nameof(HasEnvRows));
    }

    public void ToggleReveal(EnvRow row) => row.Revealed = !row.Revealed;

    // MARK: view switching

    public void RequestView(EditView requested)
    {
        if (requested != view)
        {
            if (requested == EditView.Json)
            {
                RequestJsonView();
            }
            else
            {
                AttemptSwitchToForm();
            }
        }
        Raise(nameof(View));
    }

    private void RequestJsonView()
    {
        // The JSON view renders CollapsedEnv(), which can't represent duplicate or nameless rows —
        // switching would silently drop them, bypassing the same validation Save enforces.
        if (!isRemote && EnvValidationError() is { } envError)
        {
            ValidationError = envError;
            return;
        }
        ValidationError = null;
        JsonText = CurrentFormConfig().EditorText();
        JsonError = null;
        SetView(EditView.Json);
    }

    private void AttemptSwitchToForm()
    {
        if (EffectiveJsonConfig() is not { } config)
        {
            return;
        }
        var analysis = FormMapper.Analyze(config);
        if (analysis.IsLossless)
        {
            AdoptForm(config);
            SetView(EditView.Form);
            return;
        }
        var warning = LossWarningPrefix + string.Join("\n", analysis.Lost);
        if (dialogs.Confirm(warning, null, SwitchAnywayButton, StayInJsonButton, destructive: true))
        {
            ForceSwitchToForm();
        }
    }

    private void ForceSwitchToForm()
    {
        if (EffectiveJsonConfig() is not { } config)
        {
            return;
        }
        AdoptForm(config);
        SetView(EditView.Form);
    }

    private void AdoptForm(JsonValue config)
    {
        var carried = CarriedRecords();
        var argsBefore = Args.Select(row => row.Value).ToList();
        Load(config);
        Restore(carried);
        if (!Args.Select(row => row.Value).SequenceEqual(argsBefore, StringComparer.Ordinal))
        {
            argRowsFollowOpen = false;
        }
        // A JSON edit that changed the config consumes the template, exactly
        // like a discard would — so a later Type toggle to Local re-derives
        // nothing and leaves what the user typed alone. An unchanged round
        // trip (config still equal to the template as opened) leaves the
        // flag set.
        isUntouchedTemplate = isUntouchedTemplate && config == Target.Entry.Config;
        EvaluateRequiredTool();
        RaiseAll();
    }

    /// <summary>
    /// Both row-keyed records, re-keyed onto something a rebuilt row still has. Load gives every
    /// row a new object, so without this a JSON round trip would leave the asked-for record and
    /// the published positions recognising no row at all — a synced form's placeholders would
    /// lock, a published form's argument hints would vanish. They are carried, never retaken: a
    /// retake reads current values and so would unlock nothing the user has already filled.
    ///
    /// Env rows are keyed by name, which is unique within a connector. Arguments are keyed by
    /// position, which is exact in a synced form — its JSON is read-only, so the round trip is
    /// always unchanged — and approximate only in an editable published form after a JSON edit
    /// that inserts, removes or reorders arguments, which is the one case this cannot follow.
    /// </summary>
    private sealed record Carried(
        HashSet<string> AskedEnvNames, HashSet<int> AskedArgPositions, Dictionary<int, int> OpenArgIndexByPosition);

    private Carried CarriedRecords()
    {
        var byPosition = new Dictionary<int, int>();
        var askedPositions = new HashSet<int>();
        for (var i = 0; i < Args.Count; i++)
        {
            if (askedArgs.Contains(Args[i]))
            {
                askedPositions.Add(i);
            }
            if (openArgIndexByRow.TryGetValue(Args[i], out var open))
            {
                byPosition[i] = open;
            }
        }
        return new Carried(
            EnvRows.Where(askedEnvRows.Contains).Select(r => r.Name).ToHashSet(StringComparer.Ordinal),
            askedPositions,
            byPosition);
    }

    private void Restore(Carried carried)
    {
        askedEnvRows = EnvRows.Where(r => carried.AskedEnvNames.Contains(r.Name)).ToHashSet();
        askedArgs = carried.AskedArgPositions.Where(i => i < Args.Count).Select(i => Args[i]).ToHashSet();
        openArgIndexByRow = carried.OpenArgIndexByPosition
            .Where(pair => pair.Key < Args.Count)
            .ToDictionary(pair => Args[pair.Key], pair => pair.Value);
    }

    /// <summary>
    /// Loads <paramref name="config"/> into every form/remote field and (re)computes
    /// <see cref="IsRemote"/>. The one shared place the constructor and AdoptForm funnel through,
    /// so they cannot disagree on the isRemote rule or which fields a config fills in. Tool
    /// evaluation is suppressed for the duration — both callers evaluate once themselves, after
    /// View (for ComputeRequiredTool's JSON branch) is in its final state.
    /// </summary>
    private void Load(JsonValue config)
    {
        suppressToolEvaluation = true;
        try
        {
            var model = FormMapper.Analyze(config).Model;
            Command = model.Command;
            Args.Clear();
            foreach (var arg in model.Args)
            {
                Args.Add(new ArgRow(arg));
            }
            EnvRows.Clear();
            foreach (var row in EnvRowsFrom(model.Env))
            {
                EnvRows.Add(row);   // all values re-masked
            }
            additional = model.Additional;
            var detected = RemotePattern.Detect(config);
            // The backing field, not the IsRemote setter — that setter also discards the remote
            // template's bridge invocation when switching to local, which would clobber the
            // Command/Args just loaded above from config. See EditorModel.swift's isRemoteChanged.
            isRemote = detected is not null || (Target.ForcesRemote && RemotePattern.IsRemoteShaped(config));
            // Quirk kept intentionally: RemoteUrl comes ONLY from Detect()'s canonical 2-arg shape,
            // even when IsRemote is true via the ForcesRemote/IsRemoteShaped fallback above — Decode()
            // may have found a real URL past extra flags, but the Server URL field stays blank until
            // the user (re)types it.
            remoteUrl = detected ?? "";
            if (RemotePattern.Decode(config) is { } remote)
            {
                ApplyRemoteFields(remote);
            }
            else
            {
                ResetRemoteFields();
            }
        }
        finally
        {
            suppressToolEvaluation = false;
        }
    }

    /// <summary>
    /// The blank slate every remote field starts from. Called before adopting a decoded config
    /// too: without it, switching from one auth kind to another left the old kind's fields — a
    /// bearer token, say — populated behind an auth kind that no longer shows them.
    /// </summary>
    private void ResetRemoteFields()
    {
        AuthKind = RemoteAuthKind.Automatic;
        BearerToken = "";
        HeaderName = "";
        HeaderValue = "";
        OAuthClientId = "";
        OAuthClientSecret = "";
        OAuthScopes = "";
        remoteExtraArgs = [];
        remotePassthroughEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        remotePackage = RemotePattern.DefaultPackage;
    }

    private void ApplyRemoteFields(RemoteConfig remote)
    {
        ResetRemoteFields();
        switch (remote.Auth)
        {
            case RemoteAuth.Bearer bearer:
                AuthKind = RemoteAuthKind.Bearer;
                BearerToken = bearer.Token;
                break;
            case RemoteAuth.Header header:
                AuthKind = RemoteAuthKind.Header;
                HeaderName = header.Name;
                HeaderValue = header.Value;
                break;
            case RemoteAuth.OAuthClient client:
                AuthKind = RemoteAuthKind.OAuthClient;
                OAuthClientId = client.ClientId;
                OAuthClientSecret = client.ClientSecret;
                OAuthScopes = client.Scopes;
                break;
            default:
                AuthKind = RemoteAuthKind.Automatic;
                break;
        }
        remoteExtraArgs = remote.ExtraArgs;
        remotePassthroughEnv = remote.PassthroughEnv;
        remoteLaunchStyle = remote.LaunchStyle;   // a synced Mac entry stays bare npx when edited here
        remotePackage = remote.Package;
    }

    private static IEnumerable<EnvRow> EnvRowsFrom(IReadOnlyDictionary<string, string> env) =>
        env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new EnvRow(kv.Key, kv.Value));

    // MARK: JSON

    private void ValidateJson() => JsonError = recoveredJson is null ? NotValidJson : null;

    /// <summary>Resolves the editor text via PasteRecovery, fills the name from a pasted stanza when blank, and rewrites the text to the canonical config.</summary>
    private JsonValue? EffectiveJsonConfig()
    {
        var recovered = PasteRecovery.Recover(jsonText);
        if (recovered is null)
        {
            JsonError = NotValidJson;
            return null;
        }
        JsonError = null;
        if (recovered.Name is { } pasted && Name.TrimSpaces().Length == 0)
        {
            Name = pasted;
        }
        JsonText = recovered.Config.EditorText();
        return recovered.Config;
    }

    // MARK: form → config

    private RemoteAuth CurrentRemoteAuth() => authKind switch
    {
        RemoteAuthKind.Bearer => new RemoteAuth.Bearer(bearerToken),
        RemoteAuthKind.Header => new RemoteAuth.Header(headerName, headerValue),
        RemoteAuthKind.OAuthClient => new RemoteAuth.OAuthClient(oauthClientId, oauthClientSecret, oauthScopes),
        _ => RemoteAuth.Auto,
    };

    /// <summary>The remote form as an mcp-remote invocation: what Save encodes and what the cmd.exe check judges.</summary>
    private RemoteConfig CurrentRemoteConfig() =>
        new(remoteUrl, CurrentRemoteAuth(), remoteLaunchStyle, remoteExtraArgs, remotePassthroughEnv, remotePackage);

    /// <summary>The label of the remote-form field cmd.exe would re-parse (<see cref="RemotePattern.CmdUnsafeField"/>), or null.</summary>
    private string? CmdUnsafeField() =>
        RemotePattern.CmdUnsafeField(CurrentRemoteConfig()) is { } field ? Label(field) : null;

    /// <summary>The field's caption in the Remote form, as the validation message names it.</summary>
    private static string Label(RemoteField field) => RemotePattern.FieldLabel(field);

    private JsonValue CurrentFormConfig()
    {
        if (isRemote)
        {
            var encoded = RemotePattern.Encode(CurrentRemoteConfig());
            // Preserve any unmodeled top-level keys (they can never collide with command/args/env).
            foreach (var (key, value) in additional)
            {
                encoded = encoded.With(key, value);
            }
            return encoded;
        }
        return FormMapper.Serialize(new FormModel(command, Args.Select(a => a.Value), CollapsedEnv(), additional));
    }

    /// <summary>Names are kept VERBATIM; only rows with a blank name are left out; a later duplicate wins (validation blocks that before it can lose data).</summary>
    private Dictionary<string, string> CollapsedEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in EnvRows)
        {
            if (row.Name.TrimSpaces().Length > 0)
            {
                env[row.Name] = row.Value;
            }
        }
        return env;
    }

    private string? EnvValidationError()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in EnvRows)
        {
            if (row.Name.TrimSpaces().Length == 0)
            {
                if (row.Value.Length > 0)
                {
                    return EnvNamelessError;
                }
                continue;   // a fully empty row (unused ＋ row) is just dropped
            }
            if (!seen.Add(row.Name))
            {
                return DuplicateEnvError(row.Name);
            }
        }
        return null;
    }

    // MARK: save

    /// <summary>
    /// The config this window opened on, with the <c>${CC_NEEDS:…}</c> leaves — and only those —
    /// carrying whatever the form now holds at the same JSON pointers. Anything else the fields
    /// have been talked into saying is dropped on the floor, which is the whole point: the author
    /// owns every other byte, and the next refresh would overwrite it anyway.
    /// </summary>
    private JsonValue PlaceholdersFilledIn()
    {
        var original = Target.Entry.Config;
        var candidate = view == EditView.Json
            ? PasteRecovery.Recover(jsonText)?.Config ?? original
            : CurrentFormConfig();
        var result = original;
        foreach (var (pointer, _) in Placeholder.MarkersIn(original))
        {
            if (candidate.ValueAt(pointer) is { Kind: JsonKind.String } filled)
            {
                result = result.Replacing(pointer, filled) ?? result;
            }
        }
        return result;
    }

    /// <summary>True when the entry was saved and the window should close.</summary>
    public bool Save()
    {
        ValidationError = null;
        var readOnly = IsReadOnly;
        JsonValue config;
        if (readOnly)
        {
            // Nothing a read-only window can change can be invalid: the author's own save
            // validated everything else, and a placeholder takes any text at all.
            config = PlaceholdersFilledIn();
        }
        else if (view == EditView.Json)
        {
            if (EffectiveJsonConfig() is not { } effective)
            {
                return false;
            }
            config = effective;
        }
        else
        {
            if (isRemote)
            {
                if (!RemoteUrlValid)
                {
                    ValidationError = InvalidUrlError;
                    return false;
                }
                switch (authKind)
                {
                    case RemoteAuthKind.Bearer when bearerToken.TrimSpaces().Length == 0:
                        ValidationError = BearerTokenError;
                        return false;
                    case RemoteAuthKind.Header when headerName.TrimSpaces().Length == 0:
                        ValidationError = HeaderNameError;
                        return false;
                    case RemoteAuthKind.Header when headerValue.Length == 0:
                        ValidationError = HeaderValueError;
                        return false;
                    case RemoteAuthKind.OAuthClient when oauthClientId.TrimSpaces().Length == 0:
                        ValidationError = ClientIdError;
                        return false;
                }
                if (CmdUnsafeField() is { } unsafeField)
                {
                    ValidationError = CmdUnsafeError(unsafeField);
                    return false;
                }
            }
            else if (command.TrimSpaces().Length == 0)
            {
                ValidationError = CommandError;
                return false;
            }
            if (!isRemote && EnvValidationError() is { } envError)
            {
                ValidationError = envError;
                return false;
            }
            config = CurrentFormConfig();
        }
        // Only the canonical `[-y] mcp-remote <url>` shape must carry a valid URL; extra-args invocations pass.
        if (!readOnly && RemotePattern.IsCanonicalShape(config) && RemotePattern.Detect(config) is null)
        {
            ValidationError = InvalidUrlError;
            return false;
        }
        // The editor works on a snapshot taken at window-open; if the store's copy moved underneath
        // (external edit, delete, or rename reconciled in), don't silently overwrite or resurrect it.
        McpEntry? current = null;
        if (!Target.IsNew)
        {
            if (state.Store.Collections.TryGetValue(CollectionName, out var held))
            {
                held.Mcps.TryGetValue(Target.Name, out current);
            }
            if (current?.Config != Target.Entry.Config)
            {
                var missing = current is null;
                var message = missing ? RemovedOutsideMessage(Target.Name) : ChangedOutsideMessage(Target.Name);
                var detail = missing ? RemovedOutsideDetail : ChangedOutsideDetail;
                if (!dialogs.Confirm(message, detail, SaveAnywayButton))
                {
                    return false;
                }
            }
        }
        // The name and the remembered view are the author's too, so a read-only save leaves both
        // where it found them. The enabled flag is this machine's and is carried over from the
        // store, exactly as every other save does.
        var saved = readOnly ? Target.Name : name;
        var entry = new McpEntry(current?.Enabled ?? Target.Entry.Enabled, config, readOnly ? Target.Entry.LastEditView : view);
        if (state.Upsert(saved, entry, Target.IsNew ? null : Target.Name, Target.Collection,
                         FollowedPathMarks(CollectionName, config)) is { } error)
        {
            ValidationError = error;
            return false;
        }
        if (propagate)
        {
            PropagateSavedConfig(config, saved);
        }
        CloseRequested?.Invoke();
        state.ApplyInteractively();
        return true;
    }

    /// <summary>
    /// The ticked checkbox: the same change again in each collection that held an identical copy
    /// when this window opened. Each twin keeps its own on/off state, which is this machine's
    /// business and not part of "this change"; a name already taken in one of them leaves that
    /// collection alone rather than failing a save that has already landed.
    /// <para>
    /// The checkbox promised these collections held an identical copy, and that was measured when
    /// the window opened. A twin that has moved since — a second editor window on it saved first,
    /// or an external edit reconciled in — is no longer the connector the user agreed to change,
    /// so it is skipped in silence. The same care the primary save takes over its own snapshot.
    /// </para>
    /// </summary>
    private void PropagateSavedConfig(JsonValue config, string saved)
    {
        foreach (var other in PropagateTargets)
        {
            if (state.Store.Collections.TryGetValue(other, out var held)
                && held.Mcps.TryGetValue(Target.Name, out var twin)
                && twin.Config == Target.Entry.Config)
            {
                // The twin held this window's opening config, so its marks follow the same rows.
                state.Upsert(saved, new McpEntry(twin.Enabled, config, view), Target.Name, other,
                             FollowedPathMarks(other, config));
            }
        }
    }

    /// <summary>
    /// The saved connector's path marks in <paramref name="collection"/>'s publish record,
    /// re-keyed to follow the rows they were made on, or null to leave the record as it is.
    /// <para>
    /// Each mark is first placed on the arguments the window opened on, the way the exporter
    /// places it, so a mark that had already moved before the window opened is followed from where
    /// it really was. It then goes with its row: to wherever the row sits now, recording the row's
    /// text as its value — the author may have corrected the path in place — or away with the row
    /// when the row was deleted.
    /// </para>
    /// <para>
    /// Null whenever this window cannot vouch for its rows, and publishing then places each mark by
    /// its value, refusing any it cannot: a new or read-only connector; a remote one, whose
    /// arguments are the launcher's; a save whose arguments are not the rows' (a JSON edit not
    /// brought back to the form); rows rebuilt from a JSON edit that changed the arguments; or a
    /// mark that could not be placed even on the arguments the window opened on.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<JsonPointer, PublishIntent.PathMark>? FollowedPathMarks(string collection, JsonValue config)
    {
        if (Target.IsNew || IsReadOnly || !argRowsFollowOpen
            || state.CollectionsFile.Collections.GetValueOrDefault(collection)?.Publish?.Intent.PathMarks
                   .GetValueOrDefault(Target.Name) is not { Count: > 0 } marks
            || RemotePattern.Decode(config) is not null
            || !FormMapper.Analyze(config).Model.Args.SequenceEqual(Args.Select(row => row.Value), StringComparer.Ordinal))
        {
            return null;
        }
        var placement = PublishIntent.PlacePathMarks(marks, openedArgs);
        if (placement.Unresolved.Count > 0)
        {
            return null;
        }
        // A deleted row takes its mark with it only when its text is gone from the save too. The
        // same path typed back in a new row, or moved into the command or an environment value, is
        // still the path the author marked: the record is left for publishing to place by value, or
        // to refuse.
        var model = FormMapper.Analyze(config).Model;
        var saved = model.Args.Append(model.Command).Concat(model.Env.Values).ToHashSet(StringComparer.Ordinal);
        var surviving = Args.Where(openArgIndexByRow.ContainsKey).Select(row => openArgIndexByRow[row]).ToHashSet();
        if (placement.Placed.Keys.Any(opened => !surviving.Contains(opened) && saved.Contains(openedArgs[opened])))
        {
            return null;
        }
        var followed = new Dictionary<JsonPointer, PublishIntent.PathMark>();
        for (var position = 0; position < Args.Count; position++)
        {
            if (openArgIndexByRow.TryGetValue(Args[position], out var opened)
                && placement.Placed.TryGetValue(opened, out var mark))
            {
                followed[new JsonPointer(["args", position.ToString(System.Globalization.CultureInfo.InvariantCulture)])] =
                    mark with { Value = Args[position].Value };
            }
        }
        return followed;
    }

    /// <summary>Discards all edits; nothing persisted.</summary>
    public void Cancel() => CloseRequested?.Invoke();
}
