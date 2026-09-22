using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// The harness + AppState + EditorModel construction every EditorModel test needs.
/// <paramref name="configure"/> runs on the harness BEFORE its AppState is created — for the
/// handful of tests that must seed a tool status the probe reads at launch.
/// </summary>
internal sealed class EditorRig : IDisposable
{
    public AppStateHarness H { get; }
    public AppState State { get; }

    public EditorRig(Action<AppStateHarness>? configure = null)
    {
        H = new AppStateHarness();
        configure?.Invoke(H);
        State = H.Create();
    }

    public JsonValue Local(string command, string[] args, (string Key, string Value)[]? env = null, (string Key, JsonValue Value)[]? extra = null)
    {
        var props = new List<(string, JsonValue)> { ("command", JsonValue.String(command)), ("args", JsonValue.Array(args.Select(JsonValue.String))) };
        if (env is { Length: > 0 })
        {
            props.Add(("env", JsonValue.Object(env.Select(e => (e.Key, JsonValue.String(e.Value))).ToArray())));
        }
        if (extra is not null)
        {
            props.AddRange(extra);
        }
        return JsonValue.Object(props.ToArray());
    }

    public EditorModel Editor(EditTarget target) => new(State, target, H.Dialogs, RemoteLaunchStyle.CmdNpx);

    /// <summary>
    /// An editor on a named collection's copy of a connector, read out of the store the way the
    /// Collections window's row will. A name that collection does not hold is a test bug, so it
    /// throws rather than quietly opening an empty window.
    /// </summary>
    public EditorModel Editor(string name, string collection) =>
        Editor(EditTarget.Existing(name, State.Store.Collections[collection].Mcps[name], collection));

    /// <summary>EditorModel.AuthKinds' index for <paramref name="kind"/> — the picker's own
    /// order, not an assumption about enum declaration order.</summary>
    public static int AuthKindIndexOf(RemoteAuthKind kind) => EditorModel.AuthKinds.ToList().IndexOf(kind);

    public void Dispose() => H.Dispose();
}
