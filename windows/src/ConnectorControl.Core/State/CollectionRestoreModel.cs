namespace ConnectorControl.Core.State;

/// <summary>
/// The Restore dialog's Collections tab: the lines a collection restore's confirmation lists, one per
/// connector it changes (<see cref="CollectionRestorePlan.Lines"/>), which <c>restore_collection</c> lists
/// too. Each takes the name as its caller shows it.
///
/// Mirror: Sources/ConnectorControlState/CollectionRestoreModel.swift
/// </summary>
public sealed class CollectionRestoreModel : ObservableObject
{
    /// <summary>The master list's backups, as <c>ConfigService.SaveStore</c> names them: the ones this tab and
    /// <c>list_backups</c> list, and <c>restore_collection</c> takes.</summary>
    public const string Series = "mcps";

    public static string ComesBackLine(string name, bool on) => on ? $"+ {name} (on)" : $"+ {name} (off)";
    public static string ChangesLine(string name, bool on) => on ? $"~ {name} (on)" : $"~ {name} (off)";
    /// <summary>"−" is U+2212, as <c>ControlMasking.RemovedPrefix</c> marks a removed line.</summary>
    public static string GoesLine(string name) => $"− {name}";
    public static string UnchangedLine(int count) => $"= {count} unchanged";
}
