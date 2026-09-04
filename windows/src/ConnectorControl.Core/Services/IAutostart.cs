namespace ConnectorControl.Core.Services;

public interface IAutostart
{
    /// <summary>Read fresh each time (the user may change it in Windows Settings).</summary>
    bool IsEnabled { get; }

    /// <summary>Throws <see cref="InvalidOperationException"/> carrying the OS error text on failure.</summary>
    void SetEnabled(bool enabled);
}
