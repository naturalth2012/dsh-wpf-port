using System.Windows.Threading;

namespace Dsh.Wpf;

/// <summary>
/// Extension methods for <see cref="Dispatcher"/> to reduce boilerplate when marshalling
/// calls onto the UI thread. The common pattern
/// <code>
/// if (dispatcher.CheckAccess()) action();
/// else dispatcher.Invoke(action);
/// </code>
/// is replaced by a single <c>dispatcher.Marshal(action)</c> call.
/// </summary>
internal static class DispatcherExtensions
{
    /// <summary>
    /// Execute <paramref name="action"/> on the dispatcher's thread. If already on the UI
    /// thread, runs synchronously; otherwise posts via <see cref="Dispatcher.Invoke(Action)"/>.
    /// </summary>
    public static void Marshal(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>
    /// Execute <paramref name="action"/> on the dispatcher's thread asynchronously. If already
    /// on the UI thread, runs synchronously; otherwise posts via
    /// <see cref="Dispatcher.InvokeAsync(Action)"/>.
    /// </summary>
    public static void MarshalAsync(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess())
            action();
        else
            _ = dispatcher.InvokeAsync(action);
    }

    /// <summary>
    /// Execute <paramref name="action"/> on the application's UI thread. Resolves
    /// <c>Application.Current?.Dispatcher</c> internally; if the dispatcher is null, runs
    /// synchronously as a fallback.
    /// </summary>
    public static void MarshalOnAppThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>
    /// Execute <paramref name="action"/> on the application's UI thread asynchronously.
    /// Resolves <c>Application.Current?.Dispatcher</c> internally; if the dispatcher is null,
    /// runs synchronously as a fallback.
    /// </summary>
    public static void MarshalOnAppThreadAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            _ = dispatcher.BeginInvoke(action);
    }
}
