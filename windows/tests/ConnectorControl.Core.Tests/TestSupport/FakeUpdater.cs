using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeUpdater : IUpdater
{
    public bool IsAvailable { get; set; } = true;
    public string VersionDisplay { get; set; } = "1.2.2";
    public UpdateCheck? Next { get; set; }
    public Exception? CheckFailure { get; set; }
    public Exception? DownloadFailure { get; set; }
    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public int AppliedOnQuit { get; private set; }
    public int AppliedAndRestarted { get; private set; }

    public Task<UpdateCheck?> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        if (CheckFailure is not null)
        {
            throw CheckFailure;
        }
        return Task.FromResult(Next);
    }

    public Task DownloadAsync(UpdateCheck update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        Downloads++;
        if (DownloadFailure is not null)
        {
            throw DownloadFailure;
        }
        progress?.Report(100);
        return Task.CompletedTask;
    }

    public void ApplyOnQuit(UpdateCheck update) => AppliedOnQuit++;

    public void ApplyAndRestart(UpdateCheck update) => AppliedAndRestarted++;
}
