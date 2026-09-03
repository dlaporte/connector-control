namespace ConnectorControl.Core;

public static class FormMapper
{
    private static readonly HashSet<string> FormKeys = new(["command", "args", "env"], StringComparer.Ordinal);

    public static FormAnalysis Analyze(JsonValue config)
    {
        if (config.Kind != JsonKind.Object)
        {
            return new FormAnalysis(new FormModel(), ["entire configuration (not a JSON object)"]);
        }
        var obj = config.ObjectProperties;
        var command = "";
        var args = new List<string>();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var lost = new List<string>();

        if (obj.TryGetValue("command", out var c))
        {
            if (c.Kind == JsonKind.String) { command = c.StringValue; }
            else { lost.Add($"command ({c.TypeName})"); }
        }
        if (obj.TryGetValue("args", out var a))
        {
            if (a.Kind == JsonKind.Array)
            {
                for (int i = 0; i < a.ArrayItems.Length; i++)
                {
                    var item = a.ArrayItems[i];
                    if (item.Kind == JsonKind.String) { args.Add(item.StringValue); }
                    else { lost.Add($"args[{i}] ({item.TypeName})"); }
                }
            }
            else
            {
                lost.Add("args (not an array)");
            }
        }
        if (obj.TryGetValue("env", out var e))
        {
            if (e.Kind == JsonKind.Object)
            {
                foreach (var (key, value) in e.ObjectProperties)
                {
                    if (value.Kind == JsonKind.String) { env[key] = value.StringValue; }
                    else { lost.Add($"env.{key} ({value.TypeName})"); }
                }
            }
            else
            {
                lost.Add("env (not an object)");
            }
        }
        var additional = obj.Where(p => !FormKeys.Contains(p.Key));
        lost.Sort(StringComparer.Ordinal);
        return new FormAnalysis(new FormModel(command, args, env, additional), lost);
    }

    public static JsonValue Serialize(FormModel model)
    {
        var props = new Dictionary<string, JsonValue>(model.Additional, StringComparer.Ordinal);
        // An explicit "command": "" is indistinguishable from an absent command and is
        // dropped on round-trip; save validation rejects empty commands anyway.
        if (model.Command.Length > 0)
        {
            props["command"] = JsonValue.String(model.Command);
        }
        if (model.Args.Count > 0)
        {
            props["args"] = JsonValue.Array(model.Args.Select(JsonValue.String));
        }
        if (model.Env.Count > 0)
        {
            props["env"] = JsonValue.Object(model.Env.Select(kv => new KeyValuePair<string, JsonValue>(kv.Key, JsonValue.String(kv.Value))));
        }
        return JsonValue.Object(props);
    }
}
