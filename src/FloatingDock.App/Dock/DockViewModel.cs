using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;

namespace FloatingDock.UI.Dock;

/// <summary>
/// Combines pinned apps, running windows and settings into the list of dock slots. One instance feeds every DockWindow
/// (one per monitor). All members are used on the UI thread.
/// </summary>
internal sealed class DockViewModel
{
    private readonly DockEntryReconciler _reconciler = new();
    private IReadOnlyList<WindowInfo> _windows = Array.Empty<WindowInfo>();
    private long _foreground;

    public IReadOnlyList<DockEntry> Entries { get; private set; } = Array.Empty<DockEntry>();

    /// <summary>Raised with the new entries; animate=false for the very first build.</summary>
    public event Action<IReadOnlyList<DockEntry>>? EntriesChanged;

    public void SetWindows(IReadOnlyList<WindowInfo> windows, long foreground)
    {
        _windows = windows;
        _foreground = foreground;
        Rebuild();
    }

    public void Rebuild()
    {
        var app = DockApp.Current;
        var s = app.Settings.Current;
        var opt = new DockEntryOptions(s.ShowStartButton, s.ShowLaunchpadButton, s.ShowPinned, s.ShowRunning);
        var next = _reconciler.Build(opt, app.Pinned.Snapshot(), _windows, _foreground);
        Entries = next;
        // always forwarded: even when nothing visible changed the slots must point at the current window lists
        EntriesChanged?.Invoke(next);
    }
}
