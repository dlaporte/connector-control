using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Body, string? Category)> Sent { get; } = [];

    public event Action? RestartActionActivated;

    public void Notify(string title, string body, string? category = null) => Sent.Add((title, body, category));

    /// <summary>Simulates the user clicking the toast's Restart Claude button.</summary>
    public void ActivateRestart() => RestartActionActivated?.Invoke();
}
