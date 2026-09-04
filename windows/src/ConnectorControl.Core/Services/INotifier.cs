namespace ConnectorControl.Core.Services;

public interface INotifier
{
    /// <summary>Show a notification; <paramref name="category"/> == <see cref="Notifications.RestartCategory"/> adds the Restart Claude button.</summary>
    void Notify(string title, string body, string? category = null);

    /// <summary>Raised on the UI thread when the user clicks the Restart Claude button.</summary>
    event Action? RestartActionActivated;
}
