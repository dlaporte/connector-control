using System.Collections.ObjectModel;
using System.Text;

namespace ConnectorControl.Core.State;

/// <summary>Catalog §3 EditSheetView without the pixels: every field, switch rule, validation string, and save/remove flow.</summary>
public sealed class EditorModel : ObservableObject
{
    public const string NotValidJson = "Not valid JSON — check for a stray brace, missing comma, or unquoted value.";
    public const string JsonTip = "Tip: paste a README snippet or an mcpServers stanza — a wrapper or a bare \"name\": {…} entry is unwrapped automatically, and the name filled in.";
    public const string UrlHint = "Enter a valid http(s) URL, e.g. https://example.com/mcp";
    public const string RemoteFooter = "Runs via npx mcp-remote — managed for you.";
    public const string AutomaticCaption = "Uses the server's OAuth (a browser window opens on first use), or no auth if the server is open.";
    public const string BearerCaption = "Sent as Authorization: Bearer …";
    public const string InvalidUrlError = "Server URL must be a valid http(s) URL.";
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

    public static readonly IReadOnlyList<RemoteAuthKind> AuthKinds =
        [RemoteAuthKind.Automatic, RemoteAuthKind.Bearer, RemoteAuthKind.Header, RemoteAuthKind.OAuthClient];

    public static readonly IReadOnlyList<string> AuthKindTitles = AuthKinds.Select(AuthKindTitle).ToList();

    public static string AuthKindTitle(RemoteAuthKind kind) => kind switch
    {
        RemoteAuthKind.Automatic => "Automatic (OAuth / none)",
        RemoteAuthKind.Bearer => "Bearer token",
        RemoteAuthKind.Header => "Custom header",
        RemoteAuthKind.OAuthClient => "OAuth client ID/secret",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private readonly AppState state;
    private readonly IDialogs dialogs;

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
    private IReadOnlyList<string> remoteExtraArgs = [];
    private IReadOnlyDictionary<string, string> remotePassthroughEnv = new Dictionary<string, string>(StringComparer.Ordinal);
    private RemoteLaunchStyle remoteLaunchStyle;
    private string command;
    private IReadOnlyDictionary<string, JsonValue> additional;
    private string jsonText;
    private string? jsonError;
    private string? validationError;

    public EditorModel(AppState state, EditTarget target, IDialogs dialogs, RemoteLaunchStyle newRemoteStyle)
    {
        this.state = state;
        this.dialogs = dialogs;
        Target = target;
        name = target.Name;
        view = target.Entry.LastEditView;
        var config = target.Entry.Config;
        var detected = RemotePattern.Detect(config);
        isRemote = target.ForcesRemote || detected is not null;
        remoteUrl = detected ?? "";
        var model = FormMapper.Analyze(config).Model;
        command = model.Command;
        Args = new ObservableCollection<ArgRow>(model.Args.Select(a => new ArgRow(a)));
        EnvRows = new ObservableCollection<EnvRow>(EnvRowsFrom(model.Env));
        additional = model.Additional;
        jsonText = config.EditorText();
        remoteLaunchStyle = newRemoteStyle;
        if (RemotePattern.Decode(config) is { } remote)
        {
            ApplyRemoteFields(remote);
        }
    }

    public EditTarget Target { get; }

    public string WindowTitle => Target.WindowTitle;

    public event Action? CloseRequested;

    /// <summary>Raised when a fresh env row wants keyboard focus on its name field.</summary>
    public event Action<EnvRow>? FocusEnvRowRequested;

    // MARK: view

    public EditView View
    {
        get => view;
        private set
        {
            if (Set(ref view, value))
            {
                RaiseViewFlags();
            }
        }
    }

    public bool IsFormView
    {
        get => view == EditView.Form;
        set
        {
            if (value)
            {
                RequestView(EditView.Form);
            }
            RaiseViewFlags();   // a refused switch must snap the segmented control back
        }
    }

    public bool IsJsonView
    {
        get => view == EditView.Json;
        set
        {
            if (value)
            {
                RequestView(EditView.Json);
            }
            RaiseViewFlags();
        }
    }

    private void RaiseViewFlags()
    {
        Raise(nameof(IsFormView));
        Raise(nameof(IsJsonView));
        Raise(nameof(CanSave));
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
            if (Target.IsNew && !value && View == EditView.Form && (Args.Any(a => a.Value == "mcp-remote") || Command.Length == 0))
            {
                Command = "npx";
                Args.Clear();
                Args.Add(new ArgRow("-y"));
                Args.Add(new ArgRow(""));
            }
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
                Raise(nameof(ShowUrlHint));
                Raise(nameof(CanSave));
            }
        }
    }

    /// <summary>Basic URL syntax check for the remote form: http(s) scheme and a host.</summary>
    public bool RemoteUrlValid => RemotePattern.IsValidHttpUrl(remoteUrl);

    public bool ShowUrlHint => remoteUrl.Length > 0 && !RemoteUrlValid;

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
        get => AuthKinds.ToList().IndexOf(authKind);
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

    public string Command { get => command; set => Set(ref command, value); }

    public ObservableCollection<ArgRow> Args { get; }

    public ObservableCollection<EnvRow> EnvRows { get; }

    public bool HasEnvRows => EnvRows.Count > 0;

    public bool HasAdditional => additional.Count > 0;

    public string AdditionalTitle =>
        $"{additional.Count} field(s) not editable here: {string.Join(", ", additional.Keys.Order(StringComparer.Ordinal))} — switch to JSON to edit";

    public string AdditionalPreview => Encoding.UTF8.GetString(JsonValue.Object(additional).Serialize());

    public string JsonText
    {
        get => jsonText;
        set
        {
            if (Set(ref jsonText, value))
            {
                ValidateJson();
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

    /// <summary>Catalog §3.4: Save is disabled with a JSON error, or in the remote form without a valid URL.</summary>
    public bool CanSave => !((view == EditView.Json && jsonError is not null) || (view == EditView.Form && isRemote && !RemoteUrlValid));

    public bool CanRemove => !Target.IsNew;

    // MARK: list editing (catalog §3.6)

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

    // MARK: view switching (catalog §3.5)

    public void RequestView(EditView requested)
    {
        if (requested == view)
        {
            return;
        }
        if (requested == EditView.Json)
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
            View = EditView.Json;
        }
        else
        {
            AttemptSwitchToForm();
        }
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
            AdoptForm(analysis.Model, config);
            View = EditView.Form;
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
        AdoptForm(FormMapper.Analyze(config).Model, config);
        View = EditView.Form;
    }

    private void AdoptForm(FormModel model, JsonValue config)
    {
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
        isRemote = detected is not null || (Target.ForcesRemote && RemotePattern.IsRemoteShaped(config));
        remoteUrl = detected ?? "";
        if (RemotePattern.Decode(config) is { } remote)
        {
            ApplyRemoteFields(remote);
        }
        else
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
        }
        RaiseAll();
    }

    private void ApplyRemoteFields(RemoteConfig remote)
    {
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
    }

    private static IEnumerable<EnvRow> EnvRowsFrom(IReadOnlyDictionary<string, string> env) =>
        env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new EnvRow(kv.Key, kv.Value));

    // MARK: JSON (catalog §3.7)

    private void ValidateJson() => JsonError = PasteRecovery.Recover(jsonText) is null ? NotValidJson : null;

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

    // MARK: form → config (catalog §3.5 currentFormConfig)

    private RemoteAuth CurrentRemoteAuth() => authKind switch
    {
        RemoteAuthKind.Bearer => new RemoteAuth.Bearer(bearerToken),
        RemoteAuthKind.Header => new RemoteAuth.Header(headerName, headerValue),
        RemoteAuthKind.OAuthClient => new RemoteAuth.OAuthClient(oauthClientId, oauthClientSecret, oauthScopes),
        _ => RemoteAuth.Auto,
    };

    private JsonValue CurrentFormConfig()
    {
        if (isRemote)
        {
            var encoded = RemotePattern.Encode(new RemoteConfig(remoteUrl, CurrentRemoteAuth(), remoteLaunchStyle, remoteExtraArgs, remotePassthroughEnv));
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
                return $"Duplicate environment variable name: {row.Name}";
            }
        }
        return null;
    }

    // MARK: save / remove / cancel (catalog §3.8–§3.10)

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
                var message = missing ? $"“{Target.Name}” was removed outside this editor." : $"“{Target.Name}” changed outside this editor.";
                var detail = missing ? "Saving will add it back." : "Saving will overwrite that change with this editor's version.";
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
        if (!dialogs.Confirm($"Remove “{Target.Name}”? {RemoveInformative}", null, RemoveButton, destructive: true))
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
