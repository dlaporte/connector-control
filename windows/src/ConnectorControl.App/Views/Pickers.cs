using System.Windows;
using ConnectorControl.Core.State;
using Microsoft.Win32;

namespace ConnectorControl.App.Views;

/// <summary>
/// The native pickers the windows put up, built in one place so a filter or a title cannot drift
/// between copies. Each answers with what was chosen, or null when the picker was cancelled. Owned
/// by <c>owner</c> when there is one; the flyout's are ownerless, because the flyout hides the
/// moment a picker takes the focus. The Mac mirror is its FilePanels enum.
/// </summary>
internal static class Pickers
{
    /// <summary>One collection document, for Import, Subscribe and a collection asking to be pointed at its source again.</summary>
    public static string? Document(Window? owner)
    {
        var picker = new OpenFileDialog { Title = SettingsModel.ChooseTitle, Filter = CollectionsModel.DocumentFilter, Multiselect = false };
        return Show(picker, owner) ? picker.FileName : null;
    }

    /// <summary>One folder: the store's, or the one a collection publishes to.</summary>
    public static string? Folder(Window? owner)
    {
        var picker = new OpenFolderDialog { Title = SettingsModel.ChooseTitle, Multiselect = false };
        return Show(picker, owner) ? picker.FolderName : null;
    }

    /// <summary>Where an exported collection document goes, offering <paramref name="fileName"/> to start from.</summary>
    public static string? SaveCollection(Window owner, string fileName)
    {
        var picker = new SaveFileDialog { FileName = fileName, Filter = CollectionsModel.DocumentFilter };
        return Show(picker, owner) ? picker.FileName : null;
    }

    private static bool Show(CommonDialog picker, Window? owner) =>
        (owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) == true;
}
