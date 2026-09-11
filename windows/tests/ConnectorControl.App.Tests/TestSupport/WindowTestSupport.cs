using System.Windows;
using System.Windows.Threading;

namespace ConnectorControl.App.Tests.TestSupport;

/// <summary>
/// WPF defers a binding's first target update to DataBind priority; the test host runs the
/// test body synchronously, so every window test must pump that queue before reading any
/// bound state, then force a real measure/arrange pass so layout-dependent state (visibility,
/// container generation) exists to assert against.
/// </summary>
internal static class WindowTestSupport
{
    public static void Layout(Window window, Size size)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.Measure(size);
        window.Arrange(new Rect(0, 0, size.Width, size.Height));
        window.UpdateLayout();
    }
}
