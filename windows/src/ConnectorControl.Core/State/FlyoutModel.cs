using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ConnectorControl.Core.State;

/// <summary>Catalog §2 PopoverView, minus pixels: header, error banner, rows, footer, and every action it wires.</summary>
public sealed class FlyoutModel : ObservableObject, IDisposable
{
    public const string Title = "Connector Control";
    public const string AddTooltip = "Add Connector";
    public const string SettingsTooltip = "Settings";
    public const string QuitTooltip = "Quit Connector Control";
    public const string EmptyText = "No connectors configured yet — add one below.";
    public const string RetryTitle = "Apply Failed — Retry";
    public const string RestartTitle = "Restart Required";
    public const string NewProfileTitle = "New Profile…";
    /// <summary>Segoe Fluent Icons: Warning (exclamationmark.arrow.circlepath's nearest) and Refresh (arrow.clockwise).</summary>
    public const string RetryGlyph = "";
    public const string RestartGlyph = "";
    /// <summary>Segoe Fluent Icons: Warning, on a row whose launcher is missing. The same code point the retry footer uses, named separately so changing one does not move the other.</summary>
    public const string ToolWarningGlyph = "\ue7ba";

    private readonly AppState state;
    private IReadOnlyList<ProfileMenuItem> profileItems = [];

    public FlyoutModel(AppState state)
    {
        this.state = state;
        Rows = [];
        state.PropertyChanged += OnStateChanged;
        Rebuild();
    }

    public string Subtitle => state.HeaderSubtitle;

    public string ProfileChipText => state.ActiveProfile + " ▾";

    public IReadOnlyList<ProfileMenuItem> ProfileItems => profileItems;

    public string RenameProfileTitle => $"Rename “{state.ActiveProfile}”…";

    public string DeleteProfileTitle => $"Delete “{state.ActiveProfile}”…";

    public bool CanDeleteProfile => state.ProfileNames.Count >= 2;

    public string? ErrorMessage => state.LastError;

    public bool HasError => state.LastError is not null;

    public ObservableCollection<ConnectorRow> Rows { get; }

    public bool IsEmpty => state.Store.Mcps.Count == 0;

    public FooterKind Footer => state.ApplyRetryNeeded ? FooterKind.RetryApply
        : state.NeedsClaudeRestart ? FooterKind.RestartRequired
        : FooterKind.None;

    public bool ShowFooter => Footer != FooterKind.None;

    public string FooterTitle => Footer == FooterKind.RetryApply ? RetryTitle : RestartTitle;

    public string FooterGlyph => Footer == FooterKind.RetryApply ? RetryGlyph : RestartGlyph;

    /// <summary>The Mac popover's onAppear: a routine reload on every open, then the rows' launchers.</summary>
    public void Opened()
    {
        state.Reload();
        ProbeRowTools();
    }

    /// <summary>
    /// The tools the listed connectors need that are not cached yet (addendum
    /// 2026-09-06-row-glyph §3). Nothing required, or everything cached, spawns no process;
    /// AppState coalesces a tool already in flight.
    /// </summary>
    private void ProbeRowTools()
    {
        var needed = ToolRequirement.RequiredTools(state.Store.Mcps.Values.Select(e => e.Config))
            .Where(t => !state.ToolStatuses.ContainsKey(t))
            .ToArray();
        if (needed.Length > 0)
        {
            _ = state.RefreshToolsAsync(needed);
        }
    }

    /// <summary>
    /// One row's caution-glyph tooltip, by the rule the editor and Settings also use: the
    /// entry's required tool, then that tool's cached status (addendum §2).
    /// </summary>
    private string? WarningFor(McpEntry entry)
    {
        if (ToolRequirement.RequiredTool(entry.Config) is not { } tool)
        {
            return null;
        }
        state.ToolStatuses.TryGetValue(tool, out var status);
        return ToolNote.RowWarning(tool, status);
    }

    public void SwitchProfile(string name) => state.SwitchProfile(name);

    public void NewProfile() => state.NewProfile();

    public void RenameProfile() => state.RenameProfile();

    public void DeleteProfile() => state.DeleteProfile();

    public void Quit() => state.QuitApp();

    /// <summary>The single footer button: retry the apply, or restart Claude.</summary>
    public void FooterAction()
    {
        if (state.ApplyRetryNeeded)
        {
            state.Apply();
        }
        else if (state.NeedsClaudeRestart)
        {
            _ = state.RestartClaudeAsync();
        }
    }

    /// <summary>The pencil button opens the editor only if the entry still exists in the store (catalog §2.4).</summary>
    public McpEntry? EntryFor(string name) => state.Store.Mcps.TryGetValue(name, out var entry) ? entry : null;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    /// <summary>Diffs Rows against the store so an in-flight toggle keeps its row object.</summary>
    private void Rebuild()
    {
        var names = state.SortedNames;
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!state.Store.Mcps.ContainsKey(Rows[i].Name))
            {
                Rows.RemoveAt(i);
            }
        }
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var entry = state.Store.Mcps[name];
            var enabled = entry.Enabled;
            var warning = WarningFor(entry);
            if (i < Rows.Count && Rows[i].Name == name)
            {
                Rows[i].Sync(enabled, warning);
            }
            else
            {
                var existing = Rows.FirstOrDefault(r => r.Name == name);
                if (existing is not null)
                {
                    Rows.Remove(existing);
                    existing.Sync(enabled, warning);
                    Rows.Insert(i, existing);
                }
                else
                {
                    Rows.Insert(i, new ConnectorRow(state, name, enabled, warning));
                }
            }
        }
        var active = state.ActiveProfile;
        profileItems = state.ProfileNames.Select(n => new ProfileMenuItem(n, n == active)).ToList();
        RaiseAll();
    }

    public void Dispose() => state.PropertyChanged -= OnStateChanged;
}
