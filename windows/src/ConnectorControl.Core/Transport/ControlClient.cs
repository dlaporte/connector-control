namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlClient.swift
///
/// The tool's way to the app: connect, and when nothing answers — and starting is allowed — start the app
/// in the background and wait for it, up to <see cref="StartTimeout"/>.
/// </summary>
public sealed class ControlClient
{
    public const string StartingText = "Starting Connector Control…";
    public const string NotRunningError = "Connector Control isn’t running.";
    public const string UnreachableError = "Connector Control isn’t running and couldn’t be started.";
    public const string DroppedError = "Connector Control closed the connection before answering.";
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.1);
    /// <summary>The most a line from the app may hold: far past any answer it gives, short of a stream that
    /// never ends a line.</summary>
    public const int MaxAnswerBytes = 64 * 1_048_576;

    public enum Failure
    {
        /// <summary>Nothing answered, and starting the app is not allowed.</summary>
        NotRunning,
        /// <summary>Nothing answered, and starting the app did not bring it up in time.</summary>
        CouldNotStart,
    }

    public static string Message(Failure failure) => failure == Failure.NotRunning ? NotRunningError : UnreachableError;

    /// <summary>Platform idiom: C# throws an exception where the Mac throws its <see cref="Failure"/> enum.</summary>
    public sealed class UnreachableException(Failure reason) : Exception(Message(reason))
    {
        public Failure Reason { get; } = reason;
    }

    private readonly Func<IControlStream?> connectOnce;
    private readonly Func<bool>? launch;
    private readonly Action? announce;
    private readonly Action<TimeSpan> sleep;
    private readonly Func<DateTime> now;

    /// <summary><paramref name="connectOnce"/> tries the endpoint once; <paramref name="launch"/> starts the
    /// app, null where starting is not allowed; <paramref name="announce"/> says it is being started — the
    /// command line prints <see cref="StartingText"/>, the relay says nothing on Claude's stream.</summary>
    public ControlClient(Func<IControlStream?> connectOnce, Func<bool>? launch, Action? announce,
        Action<TimeSpan>? sleep = null, Func<DateTime>? now = null)
    {
        this.connectOnce = connectOnce;
        this.launch = launch;
        this.announce = announce;
        this.sleep = sleep ?? Thread.Sleep;
        this.now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>A connection to the app; throws <see cref="UnreachableException"/> when there is none to be had.</summary>
    public IControlStream Connect()
    {
        if (connectOnce() is { } first)
        {
            return first;
        }
        if (launch is null)
        {
            throw new UnreachableException(Failure.NotRunning);
        }
        // Said only once something has been started: a tool that starts nothing says only that it could not.
        if (!launch())
        {
            throw new UnreachableException(Failure.CouldNotStart);
        }
        announce?.Invoke();
        var deadline = now() + StartTimeout;
        while (now() < deadline)
        {
            sleep(PollInterval);
            if (connectOnce() is { } stream)
            {
                return stream;
            }
        }
        throw new UnreachableException(Failure.CouldNotStart);
    }
}
