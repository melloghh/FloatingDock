namespace FloatingDock.Core.Services;

public static class ScreenCapturePolicy
{
    public static bool IsSelectionOverlay(string? executable, bool visible, bool cloaked, bool framed, bool topmost, bool coversMonitor, string? windowClass = null)
    {
        if (!visible || cloaked || !topmost || !coversMonitor || executable is null) return false;
        string name = Models.WinPath.FileName(executable);
        // New Snipping Tool uses a full-monitor XamlWindow with WS_CAPTION still set.
        bool modernSelection = name.Equals("SnippingTool.exe", StringComparison.OrdinalIgnoreCase) && windowClass is "XamlWindow" or "SnipOverlayRootWindow";
        if (framed && !modernSelection) return false;
        return name.Equals("ScreenClippingHost.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("SnippingTool.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ScreenSketch.exe", StringComparison.OrdinalIgnoreCase);
    }
}
