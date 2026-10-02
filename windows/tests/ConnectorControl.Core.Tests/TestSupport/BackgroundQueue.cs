using System.Collections.Concurrent;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// Captures AppHost.Background work so the test runs it, on its own thread, when it chooses: a
/// tool probe then never waits for a pool thread. xUnit runs test bodies on pool threads, and the
/// parallel suite parks enough of them in sleeps that a Task.Run could wait out a test's whole
/// timeout for one (the Windows CI flakes). A queue rather than inline work, so a test can still
/// tell that the work was handed off and not run on the caller's thread.
/// </summary>
public sealed class BackgroundQueue
{
    private readonly ConcurrentQueue<Action> queue = new();

    public int Pending => queue.Count;

    public void Add(Action work) => queue.Enqueue(work);

    /// <summary>Runs everything queued so far (and anything that queues); returns how many ran.</summary>
    public int RunAll()
    {
        var ran = 0;
        while (queue.TryDequeue(out var work))
        {
            work();
            ran++;
        }
        return ran;
    }
}
