using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Mirror: Tests/ConnectorControlStateTests/Transport/ControlClientTests.swift</summary>
public class ControlClientTests
{
    private sealed class NullStream : IControlStream
    {
        public byte[] Read(int maximum) => [];
        public bool Write(byte[] data) => true;
        public void Close()
        {
        }

        public void CloseWrite()
        {
        }
    }

    /// <summary>What the client's closures count and the clock they read.</summary>
    private sealed class Seen
    {
        /// <summary>Time on a monotonic clock, which the client reads as <c>now</c>.</summary>
        public TimeSpan Clock = TimeSpan.Zero;
        public int Attempts;
        public int Launches;
        public int Announced;
    }

    [Fact]
    public void AnAppThatAnswersIsUsedAsItIs()
    {
        var seen = new Seen();
        var client = new ControlClient(() => new NullStream(), () => { seen.Launches++; return true; }, () => Assert.Fail("nothing to announce"));
        client.Connect();
        Assert.Equal(0, seen.Launches);
    }

    [Fact]
    public void NothingAnsweringWhereStartingIsNotAllowedIsNotRunning()
    {
        var client = new ControlClient(() => null, null, () => Assert.Fail("nothing is started"));
        Assert.Equal(ControlClient.Failure.NotRunning, Assert.Throws<ControlClient.UnreachableException>(() => client.Connect()).Reason);
        Assert.Equal(ControlClient.NotRunningError, ControlClient.Message(ControlClient.Failure.NotRunning));
    }

    [Fact]
    public void TheAppIsStartedOnceAnnouncedOnceAndWaitedFor()
    {
        var seen = new Seen();
        var client = new ControlClient(() => ++seen.Attempts == 4 ? new NullStream() : null,
            () => { seen.Launches++; return true; }, () => seen.Announced++,
            wait => seen.Clock += wait, () => seen.Clock);
        client.Connect();
        Assert.Equal([4, 1, 1], [seen.Attempts, seen.Launches, seen.Announced]);
    }

    [Fact]
    public void AnAppThatDoesNotComeUpIn15SecondsIsUnreachable()
    {
        var seen = new Seen();
        var epoch = seen.Clock;
        var client = new ControlClient(() => null, () => true, null, wait => seen.Clock += wait, () => seen.Clock);
        Assert.Equal(ControlClient.Failure.CouldNotStart, Assert.Throws<ControlClient.UnreachableException>(() => client.Connect()).Reason);
        Assert.InRange((seen.Clock - epoch).TotalSeconds, ControlClient.StartTimeout.TotalSeconds,
            ControlClient.StartTimeout.TotalSeconds + ControlClient.PollInterval.TotalSeconds * 1.5);
        Assert.Equal(ControlClient.UnreachableError, ControlClient.Message(ControlClient.Failure.CouldNotStart));
    }

    [Fact]
    public void AnAppThatCannotBeStartedIsReportedAtOnce()
    {
        var seen = new Seen();
        var epoch = seen.Clock;
        var client = new ControlClient(() => null, () => false, () => seen.Announced++, wait => seen.Clock += wait, () => seen.Clock);
        Assert.Equal(ControlClient.Failure.CouldNotStart, Assert.Throws<ControlClient.UnreachableException>(() => client.Connect()).Reason);
        Assert.Equal(epoch, seen.Clock);
        Assert.Equal(0, seen.Announced);
    }

    /// <summary>An elevated tool starts nothing, whatever <c>launch</c> allows: the app would run elevated too, out of
    /// reach of everything the person runs as themselves. It says why, never that it is starting the app.</summary>
    [Fact]
    public void AnElevatedToolStartsNothingAndSaysWhy()
    {
        var seen = new Seen();
        var client = new ControlClient(() => { seen.Attempts++; return null; }, () => { seen.Launches++; return true; },
            () => seen.Announced++, elevated: true);
        Assert.Equal(ControlClient.Failure.Elevated, Assert.Throws<ControlClient.UnreachableException>(() => client.Connect()).Reason);
        Assert.Equal([1, 0, 0], [seen.Attempts, seen.Launches, seen.Announced]);
        Assert.Equal(ControlClient.ElevatedError, ControlClient.Message(ControlClient.Failure.Elevated));
    }

    [Fact]
    public void AnElevatedToolStillUsesAnAppThatAnswers()
    {
        var client = new ControlClient(() => new NullStream(), null, null, elevated: true);
        client.Connect();
    }
}
