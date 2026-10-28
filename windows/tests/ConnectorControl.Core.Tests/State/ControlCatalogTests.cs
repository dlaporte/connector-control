using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlCatalogTests.swift</summary>
public class ControlCatalogTests
{
    /// <summary>The spec's table: read-only, destructive, idempotent, gate class, and the command's first word(s).</summary>
    private static readonly Dictionary<string, (bool ReadOnly, bool Destructive, bool Idempotent, ControlGateClass Gate, string Usage)> Table =
        new(StringComparer.Ordinal)
        {
            ["get_status"] = (true, false, false, ControlGateClass.Read, "status"),
            ["check_tools"] = (true, false, false, ControlGateClass.Read, "doctor"),
            ["list_collections"] = (true, false, false, ControlGateClass.Read, "collections"),
            ["list_connectors"] = (true, false, false, ControlGateClass.Read, "connectors"),
            ["get_connector"] = (true, false, false, ControlGateClass.Read, "show <name>"),
            ["get_pending_update"] = (true, false, false, ControlGateClass.Read, "updates <collection>"),
            ["list_backups"] = (true, false, false, ControlGateClass.Read, "backups"),
            ["get_settings"] = (true, false, false, ControlGateClass.Read, "settings"),
            ["set_connector_enabled"] = (false, false, true, ControlGateClass.Direct, "enable <name> | disable <name>"),
            ["switch_collection"] = (false, false, true, ControlGateClass.Direct, "use <collection>"),
            ["create_collection"] = (false, false, false, ControlGateClass.Direct, "collection new <name>"),
            ["rename_collection"] = (false, false, false, ControlGateClass.Direct, "collection rename <old> <new>"),
            ["duplicate_collection"] = (false, false, false, ControlGateClass.Direct, "collection duplicate <name>"),
            ["refresh_subscription"] = (false, false, false, ControlGateClass.Direct, "refresh <collection>"),
            ["set_setting"] = (false, false, true, ControlGateClass.Direct, "settings set <key> <value>"),
            ["add_connector"] = (false, false, false, ControlGateClass.Gated, "add <name>"),
            ["subscribe"] = (false, false, false, ControlGateClass.Gated, "subscribe <file>"),
            ["restart_claude"] = (false, false, false, ControlGateClass.AlwaysConfirm, "restart-claude"),
            ["update_connector"] = (false, true, false, ControlGateClass.Gated, "edit <name>"),
            ["delete_connectors"] = (false, true, false, ControlGateClass.Gated, "delete <name>…"),
            ["delete_collection"] = (false, true, false, ControlGateClass.Gated, "collection delete <name>"),
            ["import_document"] = (false, true, false, ControlGateClass.Gated, "import <file>"),
            ["apply_update"] = (false, true, false, ControlGateClass.Gated, "apply-update <collection>"),
            ["restore_backup"] = (false, true, false, ControlGateClass.Gated, "backups restore <id>"),
        };

    [Fact]
    public void TheCatalogIsTheSpecsTable()
    {
        Assert.Equal(Table.Keys.Order(StringComparer.Ordinal), ControlTools.Catalog.Select(t => t.Name).Order(StringComparer.Ordinal));
        // Names are unique.
        Assert.Equal(ControlTools.Catalog.Count, ControlTools.Catalog.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var tool in ControlTools.Catalog)
        {
            var row = Table[tool.Name];
            Assert.Equal(new ControlToolAnnotations(row.ReadOnly, row.Destructive, row.Idempotent, false), tool.Annotations);
            Assert.Equal(row.Gate, tool.Gate);
            Assert.True(tool.CliUsage.StartsWith(row.Usage, StringComparison.Ordinal), tool.Name);
        }
    }

    [Fact]
    public void EveryToolDeclaresAllFourHints()
    {
        foreach (var tool in ControlTools.Catalog)
        {
            var descriptor = tool.McpDescriptor;
            Assert.Equal(JsonKind.Object, descriptor.Kind);
            var hints = descriptor["annotations"];
            Assert.NotNull(hints);
            Assert.Equal(new[] { "annotations", "description", "inputSchema", "name", "title" },
                descriptor.ObjectProperties.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "destructiveHint", "idempotentHint", "openWorldHint", "readOnlyHint" },
                hints.ObjectProperties.Keys.Order(StringComparer.Ordinal));
            Assert.All(hints.ObjectProperties.Values, hint => Assert.Equal(JsonKind.Bool, hint.Kind));
            Assert.Equal(JsonValue.Bool(false), hints["openWorldHint"]);
        }
    }

    /// <summary>Claude's connector settings list a tool by its title, and by its bare name without one.</summary>
    [Fact]
    public void EveryToolHasATitleThatIsNotItsName()
    {
        Assert.Equal(ControlTools.Catalog.Select(t => t.Name).Order(StringComparer.Ordinal), ControlTools.Titles.Keys.Order(StringComparer.Ordinal));
        foreach (var tool in ControlTools.Catalog)
        {
            Assert.False(string.IsNullOrEmpty(tool.Title), tool.Name);
            Assert.NotEqual(tool.Name, tool.Title);
            Assert.Equal(ControlTools.Titles[tool.Name], tool.Title);
            Assert.Equal(JsonValue.String(tool.Title), tool.McpDescriptor.ValueAt(new JsonPointer(["title"])));
        }
    }

    [Fact]
    public void EverySchemaIsAnObjectAndDeclaresWhatItRequires()
    {
        foreach (var tool in ControlTools.Catalog)
        {
            var schema = tool.InputSchema;
            Assert.Equal(JsonKind.Object, schema.Kind);
            Assert.Equal(JsonValue.String("object"), schema["type"]);
            var properties = schema["properties"];
            Assert.NotNull(properties);
            Assert.Equal(JsonKind.Object, properties.Kind);
            if (schema["required"] is { Kind: JsonKind.Array } required)
            {
                foreach (var name in required.ArrayItems.Where(item => item.Kind == JsonKind.String))
                {
                    Assert.True(properties[name.StringValue] is not null, $"{tool.Name}.{name.StringValue}");
                }
            }
        }
    }

    [Fact]
    public void EveryToolHasAGroupHelpListsAndAOneSentenceSummary()
    {
        foreach (var tool in ControlTools.Catalog)
        {
            Assert.Contains(tool.CliGroup, ControlTools.HelpGroups);
            Assert.False(string.IsNullOrEmpty(tool.Summary), tool.Name);
            Assert.False(tool.Summary.EndsWith('.'), tool.Name);
            Assert.True(tool.Description.StartsWith(tool.Summary, StringComparison.Ordinal), tool.Name);
        }
    }

    /// <summary>Help is static data, so the command-line tool can print it without an app.</summary>
    [Fact]
    public void HelpIsGeneratedFromTheToolListInItsGroups()
    {
        var lines = ControlTools.HelpText().Split('\n');
        Assert.Equal(ControlTools.HelpUsage, lines[0]);
        Assert.Equal(ControlTools.HelpOptions, lines[^1]);
        Assert.Equal(ControlTools.HelpGroups, lines.Where(line => !line.StartsWith(' ')).Skip(1).SkipLast(1));
        foreach (var tool in ControlTools.Catalog)
        {
            var line = "  " + ControlText.Pad(tool.CliUsage, Math.Max(ControlTools.HelpColumn, ControlText.Width(tool.CliUsage) + 2)) + tool.Summary;
            Assert.True(lines.Count(l => l == line) == 1, tool.CliUsage);
        }
        Assert.Contains("  status                          What Claude is running, and whether it needs a restart", lines);
        Assert.Contains("  mcp                             " + ControlTools.RelaySummary, lines);
    }
}
