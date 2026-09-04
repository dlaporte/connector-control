namespace ConnectorControl.Core.Services;

/// <summary>Sparkle's role on Windows (spec §6.7).</summary>
public interface IUpdater
{
    /// <summary>False when not running from an installed build (bare `dotnet run`, tests); every other member is then inert.</summary>
    bool IsAvailable { get; }

    /// <summary>"1.3.0", or "development build" when not installed.</summary>
    string VersionDisplay { get; }

    Task<UpdateCheck?> CheckAsync(CancellationToken cancellationToken = default);

    Task DownloadAsync(UpdateCheck update, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Stage the downloaded update to apply when the app exits (auto-update mode).</summary>
    void ApplyOnQuit(UpdateCheck update);

    /// <summary>Apply now and relaunch (the user clicked Install).</summary>
    void ApplyAndRestart(UpdateCheck update);
}
