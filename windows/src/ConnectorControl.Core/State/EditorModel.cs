using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ConnectorControl.Core.State;

/// <summary>The edit-sheet view without the pixels: every field, switch rule, validation string, and save/remove flow.</summary>
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
    /// <summary>Under the cmd /c launcher cmd.exe re-parses every argument.</summary>
    public const string CmdUnsafeSuffix = " must not contain & | < > ^ \" or spaces: on Windows the cmd /c launcher hands it to cmd.exe, which treats those as commands.";
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
    public const string RemoveButton = "Remove";
    public const string RemoveInformative = "A copy remains in Backups.";
    public const string AddArgumentTitle = "＋ Add argument";
    public const string AddVariableTitle = "＋ Add variable";
    public const string ChangedOutsideDetail = "Saving will overwrite that change with this editor's version.";
    public const string RemovedOutsideDetail = "Saving will add it back.";

    public static string DuplicateEnvError(string name) => $"Duplicate environment variable name: {name}";
    public static string RemoveMessage(string name) => $"Remove “{name}”? {RemoveInformative}";
    public static string ChangedOutsideMessage(string name) => $"“{name}” changed outside this editor.";
    public static string RemovedOutsideMessage(string name) => $"“{name}” was removed outside this editor.";

    /// <summary>The Mac's static and an instance property of the same name can coexist there; C# forbids that, so the instance property below calls this.</summary>
    public static string AdditionalTitleFor(int count, IEnumerable<string> keys) =>
        $"{count} field(s) not editable here: {string.Join(", ", keys)} — switch to JSON to edit";

    /// <summary>The picker's order, as an array so <see cref="AuthKindIndex"/> can search it without allocating.</summary>
    private static readonly RemoteAuthKind[] AuthKindOrder =
        [RemoteAuthKind.Automatic, RemoteAuthKind.Bearer, RemoteAuthKind.Header, RemoteAuthKind.OAuthClient];

    public static readonly IReadOnlyList<RemoteAuthKind> AuthKinds = AuthKindOrder;

    public static readonly IReadOnlyList<string> AuthKindTitles = AuthKinds.Select(k => k.Title()).ToList();

    private readonly AppState state;
    private readonly IDialogs dialogs;
    /// <summary>
    /// True only for a brand-new connector still showing the remote template's placeholder
    /// command/args — set once at open, never re-derived from the current field values.
    /// </summary>
    private readonly bool isUntouchedTemplate;

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
    private string? validationError;
    private Tool? requiredTool;
    private bool suppressToolEvaluation;

    public EditorModel(AppState state, EditTarget target, IDialogs dialogs, RemoteLaunchStyle newRemoteStyle)
    {
        this.state = state;
        this.dialogs = dialogs;
        Target = target;
        isUntouchedTemplate = target.IsNew && target.ForcesRemote;
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
                // Discard the remote template's bridge invocation — a local server has nothing to do with mcp-remote.
                Command = "npx";
                Args.Clear();
                Args.Add(new ArgRow("-y"));
                Args.Add(new ArgRow(""));
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

    public string BearerToken { get => bearerToken; set => Set(ref bearerToken, value); }
    public string HeaderName { get => headerName; set => Set(ref headerName, value); }
    public string HeaderValue { get => headerValue; set => Set(ref headerValue, value); }
    public string OAuthClientId { get => oauthClientId; set => Set(ref oauthClientId, value); }
    public string OAuthClientSecret { get => oauthClientSecret; set => Set(ref oauthClientSecret, value); }
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
                Raise(nameof(JsonStatusText));
                Raise(nameof(CanSave));
            }
        }
    }

    public bool HasJsonError => jsonError is not null;

    public string JsonStatusText => jsonError ?? JsonTip;

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

    /// <summary>Save is disabled with a JSON error, or in the remote form without a valid, cmd-safe URL.</summary>
    public bool CanSave => !((view == EditView.Json && jsonError is not null) || (view == EditView.Form && isRemote && !(RemoteUrlValid && RemoteUrlCmdSafe)));

    public bool CanRemove => !Target.IsNew;

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
        Load(config);
        EvaluateRequiredTool();
        RaiseAll();
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
        IsRemote = detected is not null || (Target.ForcesRemote && RemotePattern.IsRemoteShaped(config));
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
        suppressToolEvaluation = false;
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
    private static string Label(RemoteField field) => field switch
    {
        RemoteField.Url => "Server URL",
        RemoteField.HeaderName => "Header name",
        RemoteField.ClientId => "Client ID",
        RemoteField.ClientSecret => "Client Secret",
        RemoteField.Scopes => "Scopes",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

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

    // MARK: save / remove / cancel

    /// <summary>True when the entry was saved and the window should close.</summary>
    public bool Save()
    {
        ValidationError = null;
        JsonValue config;
        if (view == EditView.Json)
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
        if (RemotePattern.IsCanonicalShape(config) && RemotePattern.Detect(config) is null)
        {
            ValidationError = InvalidUrlError;
            return false;
        }
        // The editor works on a snapshot taken at window-open; if the store's copy moved underneath
        // (external edit, delete, or rename reconciled in), don't silently overwrite or resurrect it.
        McpEntry? current = null;
        if (!Target.IsNew)
        {
            state.Store.Mcps.TryGetValue(Target.Name, out current);
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
        var entry = new McpEntry(current?.Enabled ?? Target.Entry.Enabled, config, view);
        if (state.Upsert(name, entry, Target.IsNew ? null : Target.Name) is { } error)
        {
            ValidationError = error;
            return false;
        }
        CloseRequested?.Invoke();
        state.ApplyInteractively();
        return true;
    }

    /// <summary>Remove and apply in the same turn: a watcher-driven reload between the two once resurrected the connector.</summary>
    public void Remove()
    {
        if (!dialogs.Confirm(RemoveMessage(Target.Name), null, RemoveButton, destructive: true))
        {
            return;
        }
        state.Remove(Target.Name);
        state.ApplyInteractively();
        CloseRequested?.Invoke();
    }

    /// <summary>Discards all edits; nothing persisted.</summary>
    public void Cancel() => CloseRequested?.Invoke();
}
