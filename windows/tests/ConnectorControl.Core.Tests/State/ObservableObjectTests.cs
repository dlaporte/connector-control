using System.ComponentModel;
using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.State;

public class ObservableObjectTests
{
    private sealed class Sample : ObservableObject
    {
        private string name = "";
        public string Name { get => name; set => Set(ref name, value); }
        public void Everything() => RaiseAll();
    }

    [Fact]
    public void SetRaisesOnlyWhenTheValueChanges()
    {
        var sample = new Sample();
        var raised = new List<string?>();
        sample.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        sample.Name = "a";
        sample.Name = "a";
        sample.Name = "b";
        Assert.Equal(["Name", "Name"], raised);
    }

    [Fact]
    public void RaiseAllUsesTheEmptyPropertyNameWpfTreatsAsEverything()
    {
        var sample = new Sample();
        PropertyChangedEventArgs? seen = null;
        sample.PropertyChanged += (_, e) => seen = e;
        sample.Everything();
        Assert.NotNull(seen);
        Assert.Equal(string.Empty, seen.PropertyName);
    }
}
