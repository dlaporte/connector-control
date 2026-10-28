using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Body, string? Category)> Sent { get; } = [];

    public event Action? RestartActionActivated;

    /// <summary>Thrown by every notification, once recorded, when set: a toast that fails. A Swift
    /// notifier cannot throw, so the Mac's fake has no such switch.</summary>
    public Exception? Throws { get; set; }

    public void Notify(string title, string body, string? category = null)
    {
        Sent.Add((title, body, category));
        if (Throws is not null)
        {
            throw Throws;
        }
    }

    /// <summary>Simulates the user clicking the toast's Restart Claude button.</summary>
    public void ActivateRestart() => RestartActionActivated?.Invoke();
}
