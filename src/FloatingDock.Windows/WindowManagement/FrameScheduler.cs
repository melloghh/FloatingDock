using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>A sleeping high-resolution animation clock. At most one UI callback is queued; no busy wait or global timer-resolution change.</summary>
public sealed class FrameScheduler : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr callback, IntPtr argument, bool resume);
    private readonly Func<Action, bool> _post;
    private readonly Action _tick;
    private readonly Action _dispatch;
    private readonly AutoResetEvent _wake = new(false);
    private readonly EventWaitHandle _timer;
    private readonly Thread _thread;
    private int _running, _disposed, _pending, _generation, _fps = 60;
    private int _queuedGeneration;
    public bool IsRunning=>Volatile.Read(ref _running)!=0&&Volatile.Read(ref _disposed)==0;

    public FrameScheduler(Func<Action, bool> post, Action tick)
    {
        _post = post; _tick = tick; _dispatch = Dispatch;
        var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2 /* HIGH_RESOLUTION */, 0x1F0003);
        if (handle == IntPtr.Zero) handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _timer = new EventWaitHandle(false, EventResetMode.AutoReset);
        var old = _timer.SafeWaitHandle;
        _timer.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        old.Dispose();
        _thread = new Thread(Run) { IsBackground = true, Name = "FloatingDock frame pacing" };
        _thread.Start();
    }

    public void SetFrameRate(int fps)
    {
        fps = Math.Clamp(fps, 30, 144);
        if (Interlocked.Exchange(ref _fps, fps) != fps) _wake.Set();
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _running, 1);
        _wake.Set();
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        Interlocked.Increment(ref _generation);
        _wake.Set();
    }

    private void Run()
    {
        var handles = new WaitHandle[] { _wake, _timer };
        long next = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _disposed) == 0)
        {
            if (Volatile.Read(ref _running) == 0) { _wake.WaitOne(); next = Stopwatch.GetTimestamp(); continue; }
            long now = Stopwatch.GetTimestamp();
            long interval = Stopwatch.Frequency / Volatile.Read(ref _fps);
            next = Math.Max(next + interval, now + 1);
            long due = -Math.Max(1, (long)((next - now) * (10_000_000.0 / Stopwatch.Frequency)));
            if (!SetWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) return;
            if (WaitHandle.WaitAny(handles) == 0) { next = Stopwatch.GetTimestamp(); continue; }
            if (Volatile.Read(ref _running) == 0 || Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) continue;
            // The single-pending gate owns this generation until Dispatch finishes.
            // Reuse the delegate instead of allocating a closure on every frame.
            Volatile.Write(ref _queuedGeneration, Volatile.Read(ref _generation));
            if (!_post(_dispatch)) Volatile.Write(ref _pending, 0);
        }
    }

    private void Dispatch()
    {
        try { if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _running) != 0 && Volatile.Read(ref _queuedGeneration) == Volatile.Read(ref _generation)) _tick(); }
        finally { Volatile.Write(ref _pending, 0); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _wake.Set();
        // The worker only waits on our handles or enqueues a callback; it never waits for the UI thread.
        _thread.Join();
        _timer.Dispose(); _wake.Dispose();
    }
}
