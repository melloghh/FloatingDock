using FloatingDock.Core.Services;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Decides whether a top-level window is something the Windows taskbar would show. The rules live in <see cref="WindowFilterRules"/>; this class only collects the facts.</summary>
public static class WindowFilter
{
    private static readonly HashSet<string> IgnoredExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe", "searchui.exe", "shellhost.exe",
    };

    public static bool IsIgnoredExe(string? exePath) => exePath is not null && IgnoredExes.Contains(Path.GetFileName(exePath));

    /// <summary>Cheap checks that need no process access.</summary>
    public static bool IsCandidate(IntPtr hwnd, uint ownPid)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        bool minimized = NativeMethods.IsIconic(hwnd);
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        bool cloaked = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var c, sizeof(int)) == 0 && c != 0;
        var facts = new WindowFacts(
            Visible: true,
            Style: NativeMethods.GetStyle(hwnd),
            ExStyle: NativeMethods.GetExStyle(hwnd),
            HasOwner: NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero,
            Cloaked: cloaked,
            ClassName: NativeMethods.GetClass(hwnd),
            IsOwnProcess: pid == ownPid,
            Width: r.Width,
            Height: r.Height,
            Minimized: minimized);
        return WindowFilterRules.IsCandidate(facts);
    }

    public static List<IntPtr> EnumerateCandidates(uint ownPid)
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) => { if (IsCandidate(h, ownPid)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
