using System.Threading;

namespace SoulScreen.App;

/// <summary>
/// Keeps SoulScreen to one running copy per user. A second launch - a double-click on the
/// shortcut while the first is in the tray - wakes the first instead of opening a second
/// receiver that would then fail to bind the port.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SoulScreen.SingleInstance";
    private const string ActivateEventName = @"Local\SoulScreen.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>
    /// Tries to become the one instance. Returns null when another already runs, after
    /// asking it to come to the front.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        if (createdNew) return new SingleInstance(mutex, activate);

        activate.Set();
        activate.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Runs <paramref name="onActivate"/> each time another launch asks for the window.
    /// The callback is invoked on a pool thread.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration ??= ThreadPool.RegisterWaitForSingleObject(
            _activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _registration = null;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* not owned on this thread; the OS releases it on exit */ }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
