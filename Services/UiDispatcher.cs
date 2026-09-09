using System.Windows;
using System.Windows.Threading;

namespace EasyGet.Services;

internal static class UiDispatcher
{
    // Only dispatch presentation work here; download state and persistence retain
    // their own error handling. A broken display subscriber must not stop downloads.
    public static void Post(Action action) => Post(action, Application.Current?.Dispatcher);

    internal static void Post(Action action, Dispatcher? dispatcher)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RunSafely(action);
            return;
        }

        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            _ = dispatcher.BeginInvoke(() => RunSafely(action), DispatcherPriority.Background);
    }

    internal static void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("[UI presentation] {0}", exception);
        }
    }
}

/// <summary>Combines pending display refreshes without making download workers wait for the UI.</summary>
internal sealed class CoalescedUiRefresh(Action refresh, Dispatcher? dispatcher = null)
{
    private readonly Dispatcher? _dispatcher = dispatcher ?? Application.Current?.Dispatcher;
    private readonly object _inlineGate = new();
    private int _pending;

    public void Request()
    {
        if (_dispatcher is null)
        {
            lock (_inlineGate)
                UiDispatcher.RunSafely(refresh);
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished
            || Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            UiDispatcher.RunSafely(refresh);
        }, DispatcherPriority.Background);
    }
}
