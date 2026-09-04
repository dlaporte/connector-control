using System.Runtime.ExceptionServices;

namespace ConnectorControl.App.Tests.TestSupport;

/// <summary>WPF objects must be created on an STA thread; xunit test threads are MTA.</summary>
public static class StaRunner
{
    // The OS default (1 MiB on 64-bit Windows) was not enough for the first WPF
    // render on a freshly created thread: Windows CI killed the whole test
    // process with STATUS_STACK_OVERFLOW (0xC00000FD) the first time a test
    // called into DrawingVisual/RenderTargetBitmap here. A larger stack avoids it.
    private const int StackSizeBytes = 16 * 1024 * 1024;

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, StackSizeBytes);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() => result = func());
        return result;
    }
}
