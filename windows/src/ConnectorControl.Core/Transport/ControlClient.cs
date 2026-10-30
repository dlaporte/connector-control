using System.Diagnostics;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlClient.swift
///
/// The tool's way to the app: connect, and when nothing answers - and starting is allowed - start the app
/// in the background and wait for it, up to <see cref="StartTimeout"/>. An elevated tool never starts it.
/// </summary>
public sealed class ControlClient
{
    public const string StartingText = "Starting Connector Control…";
    public const string NotRunningError = "Connector Control isn’t running.";
    public const string UnreachableError = "Connector Control isn’t running and couldn’t be started.";
    public const string DroppedError = "Connector Control closed the connection before answering.";
    public const string ElevatedError = "Run connector-control without administrator rights: Connector Control runs as you, and an elevated copy can’t reach it.";
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
        /// <summary>Nothing answered, and the tool runs elevated, so it started nothing.</summary>
        Elevated,
    }

    public static string Message(Failure failure) => failure switch
    {
        Failure.NotRunning => NotRunningError,
        Failure.CouldNotStart => UnreachableError,
        Failure.Elevated => ElevatedError,
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    /// <summary>Platform idiom: C# throws an exception where the Mac throws its <see cref="Failure"/> enum.</summary>
    public sealed class UnreachableException(Failure reason) : Exception(Message(reason))
    {
        public Failure Reason { get; } = reason;
    }

    private readonly Func<IControlStream?> connectOnce;
    private readonly Func<bool>? launch;
    private readonly Action? announce;
    private readonly Action<TimeSpan> sleep;
    private readonly Func<TimeSpan> now;
    private readonly bool elevated;

    /// <summary><paramref name="connectOnce"/> tries the endpoint once; <paramref name="launch"/> starts the
    /// app, null where starting is not allowed; <paramref name="announce"/> says it is being started - the
    /// command line prints <see cref="StartingText"/>, the relay says nothing on Claude's stream.
    /// <paramref name="now"/> reads a monotonic clock, so a change to the clock cannot stretch the wait.
    /// <paramref name="elevated"/> says the tool runs with administrator rights - as root on the Mac, with an
    /// elevated token on Windows.</summary>
    public ControlClient(Func<IControlStream?> connectOnce, Func<bool>? launch, Action? announce,
        Action<TimeSpan>? sleep = null, Func<TimeSpan>? now = null, bool elevated = false)
    {
        this.connectOnce = connectOnce;
        this.launch = launch;
        this.announce = announce;
        this.sleep = sleep ?? Thread.Sleep;
        this.now = now ?? (() => Stopwatch.GetElapsedTime(0));
        this.elevated = elevated;
    }

    /// <summary>A connection to the app; throws <see cref="UnreachableException"/> when there is none to be had.</summary>
    public IControlStream Connect()
    {
        if (connectOnce() is { } first)
        {
            return first;
        }
        // An elevated tool starts nothing, whatever launch allows: the app would run elevated too, its
        // endpoint out of reach of everything the person runs as themselves, Claude among them, until quit.
        if (elevated)
        {
            throw new UnreachableException(Failure.Elevated);
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
