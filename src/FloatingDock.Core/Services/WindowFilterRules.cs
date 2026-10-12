namespace FloatingDock.Core.Services;

/// <summary>Everything the candidate decision needs to know about a top-level window (gathered by the Windows layer, so the rules are testable).</summary>
public readonly record struct WindowFacts(
    bool Visible,
    long Style,
    long ExStyle,
    bool HasOwner,
    bool Cloaked,
    string ClassName,
    bool IsOwnProcess,
    int Width,
    int Height,
    bool Minimized);

/// <summary>Would the Windows taskbar show this window? Pure rules - the Windows layer only collects the facts.</summary>
public static class WindowFilterRules
{
    public const long WS_CHILD = 0x40000000L;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "Shell_InputSwitchTopLevelWindow",
    };

    public static bool IsIgnoredClass(string? cls) => !string.IsNullOrEmpty(cls) && IgnoredClasses.Contains(cls);

    public static bool IsCandidate(in WindowFacts f)
    {
        if (!f.Visible || f.IsOwnProcess || f.Cloaked) return false;
        if ((f.Style & WS_CHILD) != 0) return false;                                   // child windows (WinEvents also fire for them) are parts of other windows
        bool appWindow = (f.ExStyle & WS_EX_APPWINDOW) != 0;
        if ((f.ExStyle & WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;           // tool windows / palettes
        if ((f.ExStyle & WS_EX_NOACTIVATE) != 0 && !appWindow) return false;           // toasts, tooltips, overlays that never take focus
        if (!appWindow && f.HasOwner) return false;                                    // dialogs belong to their owner's taskbar button
        // click-through topmost overlays (screen annotation, FPS counters) are not taskbar windows
        if (!appWindow && (f.ExStyle & WS_EX_TRANSPARENT) != 0 && (f.ExStyle & WS_EX_LAYERED) != 0 && (f.ExStyle & WS_EX_TOPMOST) != 0) return false;
        if (IsIgnoredClass(f.ClassName)) return false;
        // a window without area is not a window the user can see; a minimized window legitimately reports a tiny / parked rectangle
        if (!f.Minimized && (f.Width <= 0 || f.Height <= 0)) return false;
        return true;
    }
}
