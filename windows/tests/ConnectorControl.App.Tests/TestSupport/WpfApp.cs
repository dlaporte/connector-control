using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace ConnectorControl.App.Tests.TestSupport;

/// <summary>
/// One STA thread for the whole test run, hosting the single Application a
/// process may own, with the Fluent theme and Styles/Shared.xaml merged — so
/// windows constructed here resolve StaticResource/DynamicResource lookups
/// exactly as in the app. Every access is marshalled through its dispatcher.
/// </summary>
public static class WpfApp
{
    private static readonly Lazy<Dispatcher> Host = new(Start);

    public static void Invoke(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Host.Value.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        failure?.Throw();
    }

    public static T Invoke<T>(Func<T> func)
    {
        T result = default!;
        // Statement body: an expression-bodied lambda (() => result = func()) is also
        // convertible to Func<T> and binds to this very overload instead of Invoke(Action),
        // recursing into itself until the stack overflows.
        Invoke(() => { result = func(); });
        return result;
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // ThemeMode is experimental; the app sets it in App.xaml, tests must set it in code
            app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/ConnectorControl;component/Styles/Shared.xaml", UriKind.Relative)));
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WpfApp test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
