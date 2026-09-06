using ConnectorControl.App.Tray;

namespace ConnectorControl.App.Tests;

public class TaskbarThemeTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    [InlineData("1", false)]
    public void InterpretsTheSystemUsesLightThemeValue(object? value, bool expected)
    {
        Assert.Equal(expected, TaskbarTheme.IsLight(value));
    }

    [Fact]
    public void ReadingTheRegistryNeverThrows()
    {
        Assert.IsType<bool>(TaskbarTheme.IsLight());
    }
}
