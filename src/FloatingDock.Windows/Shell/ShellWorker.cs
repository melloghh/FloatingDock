using System.Collections.Concurrent;

namespace FloatingDock.Windows.Shell;

/// <summary>
/// One dedicated STA thread for every Shell / COM query (icon extraction, AppsFolder, property stores, shortcut resolving).
/// Keeps the UI thread free and gives the shell the apartment model it expects. Work items run strictly in order.
/// </summary>
public sealed class ShellWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public ShellWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "FloatingDock.ShellWorker" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
                try { work(); } catch { /* individual items report through their TaskCompletionSource */ }
        }
        finally { _queue.Dispose(); }
    }

    public Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _disposed) != 0) { tcs.SetCanceled(); return tcs.Task; }
        try
        {
            _queue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
        }
        catch (ObjectDisposedException) { tcs.SetCanceled(); }
        catch (InvalidOperationException) { tcs.SetCanceled(); }
        return tcs.Task;
    }

    public Task Run(Action action) => Run<object?>(() => { action(); return null; });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        if (_thread != Thread.CurrentThread) _thread.Join(2000);
    }
}
