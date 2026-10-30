using ConnectorControl.App.Views;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests;

/// <summary>What Show in Explorer asks Explorer for, without launching it.</summary>
public class ExplorerRevealTests
{
    [Fact]
    public void AFileOrFolderThatIsThereIsSelectedInItsFolder()
    {
        using var dir = new TempDir("reveal");
        var file = dir.File("team.json");
        File.WriteAllText(file, "{}");
        Assert.Equal($"/select,\"{file}\"", ExplorerReveal.Arguments(file));
        Assert.Equal($"/select,\"{dir.Path}\"", ExplorerReveal.Arguments(dir.Path));
    }

    /// <summary>Explorer's /select on a missing path opens an unrelated default location, so a path
    /// that has gone opens its parent instead.</summary>
    [Fact]
    public void APathThatHasGoneOpensItsParent()
    {
        using var dir = new TempDir("reveal");
        Assert.Equal($"\"{dir.Path}\"", ExplorerReveal.Arguments(dir.File("gone.json")));
    }
}
