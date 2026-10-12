using Microsoft.Win32;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

/// <summary>Start with Windows: a value in HKCU\...\Run (no admin rights, removed again when the option is turned off).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FloatingDock";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return k?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) k.SetValue(ValueName, $"\"{exePath}\" --autostart", RegistryValueKind.String);
        else k.DeleteValue(ValueName, false);
    }
}

/// <summary>Only one Dock per user session. The second launch tells the first one to open the settings and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly EventWaitHandle _quit;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _listener;
    private int _disposed;

    public bool IsFirst { get; }

    public SingleInstance(string name = "FloatingDock")
    {
        _mutex = new Mutex(false, $@"Local\{name}.SingleInstance");
        try { IsFirst = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsFirst = true; }
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activate");
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Quit");
    }

    /// <summary>Asks a running instance to exit in an orderly way (it restores the taskbar first). Returns false when none is running.</summary>
    public static bool RequestQuit(string name = "FloatingDock")
    {
        if (!EventWaitHandle.TryOpenExisting($@"Local\{name}.Quit", out var h)) return false;
        using (h) return h.Set();
    }

    /// <summary>Waits until the running instance has released the single-instance mutex (= has exited).</summary>
    public static bool WaitUntilStopped(TimeSpan timeout, string name = "FloatingDock")
    {
        var end = DateTime.UtcNow + timeout;
        do
        {
            using var m = new Mutex(false, $@"Local\{name}.SingleInstance");
            bool acquired;
            try { acquired = m.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (acquired) { m.ReleaseMutex(); return true; }
            Thread.Sleep(150);
        } while (DateTime.UtcNow < end);
        return false;
    }

    /// <summary>Wakes the running instance (called by the second launch).</summary>
    public void SignalFirstInstance() => _signal.Set();

    /// <summary>Calls <paramref name="onSignal"/> (on a pool thread) whenever a later launch signals this instance.</summary>
    public void ListenForSignals(Action onSignal, Action? onQuit = null)
    {
        var token = _cts.Token;
        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _signal, _quit, token.WaitHandle };
            int i;
            while ((i = WaitHandle.WaitAny(handles)) != 2)
            {
                try { if (i == 0) onSignal(); else onQuit?.Invoke(); } catch { /* ignore */ }
            }
        }) { IsBackground = true, Name = "FloatingDock.SingleInstance" };
        _listener.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        if (_listener is not null && _listener != Thread.CurrentThread) _listener.Join(2000);
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
        _signal.Dispose();
        _quit.Dispose();
        _cts.Dispose();
    }
}

/// <summary>The real Start menu. Windows has no public API to open it, so (like the Windhawk mod) a Windows-key tap is synthesised.</summary>
public static class StartButton
{
    public static void ToggleStartMenu() => Tap(NativeMethods.VK_LWIN);

    /// <summary>Win+X "power user" menu.</summary>
    public static void ShowWinXMenu()
    {
        var inputs = new[] { Key(NativeMethods.VK_LWIN, false), Key(NativeMethods.VK_X, false), Key(NativeMethods.VK_X, true), Key(NativeMethods.VK_LWIN, true) };
        NativeMethods.SendInput((uint)inputs.Length, inputs, global::System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static void Tap(ushort vk)
    {
        var inputs = new[] { Key(vk, false), Key(vk, true) };
        NativeMethods.SendInput((uint)inputs.Length, inputs, global::System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } },
    };
}
