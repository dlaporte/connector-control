using System.Windows;
using System.Windows.Threading;
using ConnectorControl.Core.State;

namespace ConnectorControl.App;

/// <summary>The WPF dispatcher as an <see cref="AppHost"/>.</summary>
public static class UiThread
{
    public static AppHost Host() => new(Marshal, Delay, () => DateTime.UtcNow);

    /// <summary>
    /// POSTS to the UI thread; never blocks. FileWatcher marshals from a timer thread and
    /// the toast notifier from a COM MTA thread, so a Dispatcher.Invoke here would deadlock
    /// the moment either side ever waits on the other (Phase 2 review). Already on the UI
    /// thread, the action runs inline so state changes keep their obvious ordering.
    /// </summary>
    public static void Marshal(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public static void Delay(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Application.Current.Dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
