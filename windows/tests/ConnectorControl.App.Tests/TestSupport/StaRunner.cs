using System.Runtime.ExceptionServices;

namespace ConnectorControl.App.Tests.TestSupport;

/// <summary>WPF objects must be created on an STA thread; xunit test threads are MTA.</summary>
public static class StaRunner
{
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
        });
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
