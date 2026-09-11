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

    /// <summary>
    /// The bridge package specifier as written — <c>mcp-remote</c>, or <c>mcp-remote@0.1.16</c> when the
    /// user pinned a version. A pin is a supply-chain control: it survives every edit.
    /// </summary>
    public string Package { get; }

    public RemoteConfig(
        string url,
        RemoteAuth auth,
        RemoteLaunchStyle launchStyle,
        IEnumerable<string>? extraArgs = null,
        IEnumerable<KeyValuePair<string, string>>? passthroughEnv = null,
        string package = RemotePattern.DefaultPackage)
    {
        Url = url;
        Auth = auth;
        ExtraArgs = extraArgs?.ToList() ?? [];
        PassthroughEnv = passthroughEnv is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(passthroughEnv, StringComparer.Ordinal);
        LaunchStyle = launchStyle;
        Package = package;
    }

    public bool Equals(RemoteConfig? other) =>
        other is not null
        && string.Equals(Url, other.Url, StringComparison.Ordinal)
        && Auth.Equals(other.Auth)
        && ExtraArgs.SequenceEqual(other.ExtraArgs, StringComparer.Ordinal)
        && DictionaryEquality.Equal(PassthroughEnv, other.PassthroughEnv)
        && LaunchStyle == other.LaunchStyle
        && string.Equals(Package, other.Package, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RemoteConfig);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Url, StringComparer.Ordinal);
        hash.Add(Auth);
        foreach (var arg in ExtraArgs) { hash.Add(arg, StringComparer.Ordinal); }
        hash.Add(DictionaryEquality.Hash(PassthroughEnv));
        hash.Add(LaunchStyle);
        hash.Add(Package, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
