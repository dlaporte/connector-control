namespace ConnectorControl.Core;

/// <summary>The pieces of an mcp-remote invocation the Remote form edits, plus whatever it doesn't model.</summary>
public sealed class RemoteConfig : IEquatable<RemoteConfig>
{
    public string Url { get; }
    public RemoteAuth Auth { get; }
    /// <summary>mcp-remote flags this app has no widget for — preserved verbatim, in order.</summary>
    public IReadOnlyList<string> ExtraArgs { get; }
    /// <summary>Env vars other than the one auth uses to indirect a header value.</summary>
    public IReadOnlyDictionary<string, string> PassthroughEnv { get; }
    /// <summary>Which launcher the config was decoded from; re-encoded in the same style.</summary>
    public RemoteLaunchStyle LaunchStyle { get; }

    public RemoteConfig(
        string url,
        RemoteAuth auth,
        IEnumerable<string>? extraArgs = null,
        IEnumerable<KeyValuePair<string, string>>? passthroughEnv = null,
        RemoteLaunchStyle launchStyle = RemoteLaunchStyle.Npx)
    {
        Url = url;
        Auth = auth;
        ExtraArgs = extraArgs?.ToList() ?? [];
        PassthroughEnv = passthroughEnv is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(passthroughEnv, StringComparer.Ordinal);
        LaunchStyle = launchStyle;
    }

    public bool Equals(RemoteConfig? other) =>
        other is not null
        && string.Equals(Url, other.Url, StringComparison.Ordinal)
        && Auth.Equals(other.Auth)
        && ExtraArgs.SequenceEqual(other.ExtraArgs, StringComparer.Ordinal)
        && DictionaryEquality.Equal(PassthroughEnv, other.PassthroughEnv)
        && LaunchStyle == other.LaunchStyle;

    public override bool Equals(object? obj) => Equals(obj as RemoteConfig);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Url, StringComparer.Ordinal);
        hash.Add(Auth);
        foreach (var arg in ExtraArgs) { hash.Add(arg, StringComparer.Ordinal); }
        hash.Add(DictionaryEquality.Hash(PassthroughEnv));
        hash.Add(LaunchStyle);
        return hash.ToHashCode();
    }

    public override string ToString() => $"RemoteConfig({Url}, {Auth}, style={LaunchStyle})";
}
