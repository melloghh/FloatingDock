using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FloatingDock.Engine;

/// <summary>
/// The engine's "UI thread": a plain Win32 message loop. WinEvent hooks and the shell services need a thread that pumps messages and
/// the running-apps service wants all its state touched from one thread, so IPC handlers hop onto this one.
/// </summary>
internal sealed class UiLoop
{
    private const uint WM_APP = 0x8000;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private readonly ConcurrentQueue<Action> _queue = new();
    private uint _threadId;

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        uint id = Volatile.Read(ref _threadId);
        if (id != 0) PostThreadMessage(id, WM_APP, UIntPtr.Zero, IntPtr.Zero);
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Quit()
    {
        uint id = Volatile.Read(ref _threadId);
        if (id != 0) PostThreadMessage(id, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Runs until <see cref="Quit"/>. Must be called on the thread that owns the hooks and windows.</summary>
    public void Run()
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);          // creates this thread's message queue
        Volatile.Write(ref _threadId, GetCurrentThreadId());
        Drain();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WM_APP && msg.Hwnd == IntPtr.Zero) { Drain(); continue; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void Drain()
    {
        while (_queue.TryDequeue(out var action))
            try { action(); } catch (Exception ex) { EngineLog.Error("ui action failed", ex); }
    }
}
