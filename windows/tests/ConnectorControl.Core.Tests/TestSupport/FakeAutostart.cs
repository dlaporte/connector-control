using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeAutostart : IAutostart
{
    public bool Enabled { get; set; }
    /// <summary>When set, SetEnabled throws InvalidOperationException with this message.</summary>
    public string? FailWith { get; set; }
    public int SetCalls { get; private set; }

    public bool IsEnabled => Enabled;

    public void SetEnabled(bool enabled)
    {
        SetCalls++;
        if (FailWith is not null)
        {
            throw new InvalidOperationException(FailWith);
        }
        Enabled = enabled;
    }
}
