using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Windows.Integration;

/// <summary>
/// A hidden top-level window that receives what WinUI does not expose: global hotkeys, display / work-area changes, the tray icon callback,
/// the "taskbar re-created" broadcast (Explorer restart) and the end-of-session notification.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const string ClassName = "FloatingDock.MessageWindow";
    public const uint TrayCallbackMessage = NativeMethods.WM_APP + 1;
    private static readonly WndProc ProcDelegate = StaticProc;     // static: lives as long as the process
    private static readonly Dictionary<IntPtr, MessageWindow> Instances = new();
    private static bool _classRegistered;

    private readonly uint _taskbarCreatedMessage;
    private readonly uint _shellMessage;
    private readonly HashSet<int> _hotkeys = new();

    public IntPtr Handle { get; private set; }
    public bool AnimationRectHookRegistered { get; private set; }
    public Func<IntPtr, RECT?>? AnimationRectProvider { get; set; }
    public int AnimationRectsUpdated { get; private set; }
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr hwnd);

    public event Action<int>? HotkeyPressed;
    public event Action? DisplayChanged;
    public event Action? WorkAreaOrSettingsChanged;
    public event Action? TaskbarCreated;
    public event Action? InputLanguageChanged;
    public event Action<uint>? TrayMessage;          // the mouse message (WM_LBUTTONUP, WM_RBUTTONUP, ...)
    public event Action? SessionEnding;

    public MessageWindow()
    {
        var inst = NativeMethods.GetModuleHandle(null);
        if (!_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcDelegate),
                hInstance = inst,
                lpszClassName = ClassName,
            };
            if (NativeMethods.RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            _classRegistered = true;
        }
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _shellMessage = NativeMethods.RegisterWindowMessage("SHELLHOOK");
        Handle = NativeMethods.CreateWindowEx((uint)NativeMethods.WS_EX_TOOLWINDOW, ClassName, "FloatingDock", (uint)NativeMethods.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        lock (Instances) Instances[Handle] = this;
        AnimationRectHookRegistered = _shellMessage != 0 && RegisterShellHookWindow(Handle);
    }

    /// <summary>Registers a global hotkey; returns false if the combination is taken by another program.</summary>
    public bool RegisterHotkey(int id, uint modifiers, uint vk)
    {
        UnregisterHotkey(id);
        if (!NativeMethods.RegisterHotKey(Handle, id, modifiers | 0x4000, vk)) return false;
        _hotkeys.Add(id);
        return true;
    }

    public void UnregisterHotkey(int id)
    {
        if (_hotkeys.Remove(id)) NativeMethods.UnregisterHotKey(Handle, id);
    }

    private static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        MessageWindow? w;
        lock (Instances) Instances.TryGetValue(hwnd, out w);
        if (w is null) return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        try { return w.HandleMessage(hwnd, msg, wParam, lParam); }
        catch (Exception) { return IntPtr.Zero; }       // never let an exception cross the native boundary
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            AnimationRectHookRegistered = _shellMessage != 0 && RegisterShellHookWindow(Handle);
            TaskbarCreated?.Invoke(); return IntPtr.Zero;
        }
        if (msg == _shellMessage && wParam.ToInt64() == 5 && AnimationRectProvider is { } provider &&
            ShellAnimationRect.TryUpdate(lParam, provider))
        {
            AnimationRectsUpdated++;
            return new IntPtr(1);
        }
        if (msg == _shellMessage && wParam.ToInt64() == 8) { InputLanguageChanged?.Invoke(); return IntPtr.Zero; }
        switch (msg)
        {
            case NativeMethods.WM_HOTKEY: HotkeyPressed?.Invoke((int)wParam); return IntPtr.Zero;
            case NativeMethods.WM_DISPLAYCHANGE: DisplayChanged?.Invoke(); return IntPtr.Zero;
            case NativeMethods.WM_SETTINGCHANGE: WorkAreaOrSettingsChanged?.Invoke(); return IntPtr.Zero;
            case TrayCallbackMessage: TrayMessage?.Invoke((uint)(lParam.ToInt64() & 0xFFFF)); return IntPtr.Zero;
            case NativeMethods.WM_QUERYENDSESSION: return new IntPtr(1);
            case NativeMethods.WM_ENDSESSION: if (wParam != IntPtr.Zero) SessionEnding?.Invoke(); return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var id in _hotkeys.ToList()) UnregisterHotkey(id);
        if (Handle != IntPtr.Zero)
        {
            if (AnimationRectHookRegistered) DeregisterShellHookWindow(Handle);
            AnimationRectHookRegistered = false;
            AnimationRectProvider = null;
            lock (Instances) Instances.Remove(Handle);
            NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
