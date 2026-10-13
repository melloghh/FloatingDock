using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using System.Runtime.InteropServices;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Brings application windows to the front (restoring minimized ones). Uses only standard window operations; never touches Windows' own animations.</summary>
public static class WindowActivator
{
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool viaAltTab);
    public enum ActivationResult { Activated, Minimized, NoLiveWindow, Denied }

    /// <summary>Verifies current HWND/PID, restores asynchronously and activates a modal popup when its owner is disabled.</summary>
    public static async Task<ActivationResult> ActivateOrMinimizeAsync(IReadOnlyList<WindowInfo> windows, bool minimizeIfActive,Func<IntPtr,bool>? minimize=null,Action<WindowInfo>? prepare=null)
    {
        var live = windows.Where(w => IsLive(w)).Select(w => w.Hwnd).ToHashSet();
        if (live.Count == 0) return ActivationResult.NoLiveWindow;
        var foreground = NativeMethods.GetForegroundWindow();
        var owner = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOTOWNER);
        long root = (owner == IntPtr.Zero ? foreground : owner).ToInt64();
        if (minimizeIfActive && live.Contains(root) && !NativeMethods.IsIconic(new IntPtr(root)))
        {
            if(minimize?.Invoke(new(root))!=true)NativeMethods.ShowWindowAsync(new IntPtr(root), NativeMethods.SW_MINIMIZE);
            return ActivationResult.Minimized;
        }
        IntPtr target = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (live.Contains(hwnd.ToInt64())) { target = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) return ActivationResult.NoLiveWindow;
        // Choose the owner and the repeat-click intent before workspace preparation can restore/focus it.
        prepare?.Invoke(windows.First(w=>w.Hwnd==target.ToInt64()));
        // An owned dialog, rather than its disabled owner, is the usable window.
        for (int i = 0; i < 8; i++)
        {
            var popup = GetLastActivePopup(target);
            if (popup == IntPtr.Zero || popup == target || !NativeMethods.IsWindowVisible(popup)) break;
            target = popup;
            if (IsWindowEnabled(target)) break;
        }
        for (int i = 0; i < 4; i++)
        {
            if (!NativeMethods.IsWindow(target)) return ActivationResult.NoLiveWindow;
            Activate(target);
            if (i == 1 && (NativeMethods.GetForegroundWindow() != target || NativeMethods.IsIconic(target)))
            {
                // Compatibility fallback for windows whose higher-integrity thread rejects input attachment (e.g. Task Manager).
                // The result is still verified; never report activation merely because an API was called.
                try { SwitchToThisWindow(target, true); }
                catch (EntryPointNotFoundException) { }
            }
            await Task.Delay(65);
            var active = NativeMethods.GetForegroundWindow();
            if (!NativeMethods.IsIconic(target) && (active == target || NativeMethods.GetAncestor(active, NativeMethods.GA_ROOTOWNER) == target)) return ActivationResult.Activated;
        }
        return ActivationResult.Denied;
    }

    public static bool IsLive(WindowInfo window)
    {
        var hwnd = new IntPtr(window.Hwnd);
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != window.ProcessId)
        {
            // Packaged apps can be hosted by ApplicationFrameHost: the snapshot PID is the app's child CoreWindow.
            if (NativeMethods.GetClass(hwnd) != "ApplicationFrameWindow") return false;
            bool childMatches = false;
            NativeMethods.EnumChildWindows(hwnd, (child, _) => { NativeMethods.GetWindowThreadProcessId(child, out var childPid); if (childPid == window.ProcessId) childMatches = true; return !childMatches; }, IntPtr.Zero);
            if (!childMatches) return false;
        }
        return NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }
    /// <summary>Activates the top-most window of the app; if one of its windows is already active and <paramref name="minimizeIfActive"/> is set, minimizes it.</summary>
    public static void ActivateOrMinimize(IReadOnlyList<WindowInfo> windows, bool minimizeIfActive)
    {
        if (windows.Count == 0) return;
        var ids = new HashSet<long>(windows.Select(w => w.Hwnd));
        var fgRoot = NativeMethods.GetForegroundWindow();
        var fgOwner = fgRoot == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(fgRoot, NativeMethods.GA_ROOTOWNER);
        long fg = (fgOwner == IntPtr.Zero ? fgRoot : fgOwner).ToInt64();
        if (minimizeIfActive && ids.Contains(fg) && !NativeMethods.IsIconic(new IntPtr(fg)))
        {
            NativeMethods.ShowWindowAsync(new IntPtr(fg), NativeMethods.SW_MINIMIZE);
            return;
        }
        // z-order from EnumWindows (top first): the most recently used window of the app comes first
        IntPtr target = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) =>
        {
            if (ids.Contains(h.ToInt64()) && !NativeMethods.IsIconic(h)) { target = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero)
        {
            NativeMethods.EnumWindows((h, _) => { if (ids.Contains(h.ToInt64())) { target = h; return false; } return true; }, IntPtr.Zero);
        }
        if (target != IntPtr.Zero) Activate(target);
    }

    public static void Activate(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        if (NativeMethods.SetForegroundWindow(hwnd) && NativeMethods.GetForegroundWindow() == hwnd) return;

        // Foreground lock: attach to the current foreground thread so SetForegroundWindow is allowed.
        var fg = NativeMethods.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
        uint me = NativeMethods.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && NativeMethods.AttachThreadInput(me, fgThread, true);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        bool targetAttached = targetThread != me && targetThread != fgThread && NativeMethods.AttachThreadInput(me, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (targetAttached) NativeMethods.AttachThreadInput(me, targetThread, false);
            if (attached) NativeMethods.AttachThreadInput(me, fgThread, false);
        }
    }

    /// <summary>Gives the app windows the standard close request (WM_CLOSE), exactly like the taskbar's "Close window".</summary>
    public static void RequestClose(IReadOnlyList<WindowInfo> windows)
    {
        foreach (var w in windows) NativeMethods.PostMessage(new IntPtr(w.Hwnd), 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}
