using FloatingDock.Core.Models;

namespace FloatingDock.Core.Services;

public sealed record DockEntryOptions(
    bool ShowStart, bool ShowLaunchpad, bool ShowPinned, bool ShowRunning);

/// <summary>
/// Merges the three independent inputs (pinned apps, running windows, special items) into the ordered list of dock slots.
/// Keeps the order of un-pinned running apps stable (first seen first). Pure and unit-tested; has no OS dependency.
/// </summary>
public sealed class DockEntryReconciler
{
    public const string StartKey = "special:start";
    public const string LaunchpadKey = "special:launchpad";
    public const string SeparatorKey = "special:separator";

    private readonly List<string> _runningOrder = new();

    public IReadOnlyList<DockEntry> Build(
        DockEntryOptions opt,
        IReadOnlyList<AppRef> pinned,
        IReadOnlyList<WindowInfo> windows,
        long foregroundHwnd)
    {
        var result = new List<DockEntry>();
        if (opt.ShowStart) result.Add(new DockEntry { Kind = DockEntryKind.Start, Key = StartKey, Name = "Start" });
        if (opt.ShowLaunchpad) result.Add(new DockEntry { Kind = DockEntryKind.Launchpad, Key = LaunchpadKey, Name = "Launchpad" });

        // 1. assign every window to the first pinned app that claims it
        var pinnedWindows = new List<WindowInfo>?[pinned.Count];
        var unclaimed = new List<WindowInfo>();
        foreach (var w in windows)
        {
            int idx = -1;
            for (int i = 0; i < pinned.Count; i++)
                if (AppMatching.Matches(pinned[i], w)) { idx = i; break; }
            if (idx >= 0) (pinnedWindows[idx] ??= new()).Add(w);
            else unclaimed.Add(w);
        }

        // 2. group the rest by program
        var groups = new Dictionary<string, List<WindowInfo>>();
        foreach (var w in unclaimed)
        {
            var key = AppMatching.GroupKey(w);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(w);
        }
        // pinned apps that are hidden from the dock still show up while running
        var hiddenPinnedRunning = new List<(AppRef App, List<WindowInfo> Windows)>();
        if (!opt.ShowPinned)
            for (int i = 0; i < pinned.Count; i++)
                if (pinnedWindows[i] is { Count: > 0 } l) hiddenPinnedRunning.Add((pinned[i], l));

        // 3. maintain first-seen order of the un-pinned running keys
        var runningKeys = new HashSet<string>(groups.Keys);
        foreach (var h in hiddenPinnedRunning) runningKeys.Add(h.App.Key);
        _runningOrder.RemoveAll(k => !runningKeys.Contains(k));
        foreach (var k in runningKeys.OrderBy(k => k, StringComparer.Ordinal))
            if (!_runningOrder.Contains(k)) _runningOrder.Add(k);

        bool IsActive(List<WindowInfo>? ws) => ws is not null && foregroundHwnd != 0 && ws.Any(w => w.Hwnd == foregroundHwnd);

        int pinnedAdded = 0;
        if (opt.ShowPinned)
        {
            var seen = new HashSet<string>();
            var shown = new List<AppRef>();
            for (int i = 0; i < pinned.Count; i++)
            {
                var app = pinned[i];
                if (!seen.Add(app.Key)) continue;       // the same application pinned twice is shown once
                if (app.IsSeparator)
                {
                    result.Add(new DockEntry { Kind = DockEntryKind.Separator, Key = app.Key, App = app, IsPinned = true });
                    continue;
                }
                if (shown.Any(o => AppMatching.AreEquivalent(o, app))) continue;      // ... also when it was pinned once by exe and once by AUMID
                shown.Add(app);
                var ws = pinnedWindows[i];
                // a duplicate pin shares windows with the first one; merge so that no window is lost
                result.Add(new DockEntry
                {
                    Kind = DockEntryKind.App, Key = app.Key, Name = app.Name, App = app, IsPinned = true,
                    IsActive = IsActive(ws), Windows = (IReadOnlyList<WindowInfo>?)ws ?? Array.Empty<WindowInfo>(),
                });
                pinnedAdded++;
            }
        }

        if (opt.ShowRunning || !opt.ShowPinned)
        {
            var running = new List<DockEntry>();
            foreach (var key in _runningOrder)
            {
                if (groups.TryGetValue(key, out var ws))
                {
                    if (!opt.ShowRunning) continue;
                    var app = AppMatching.FromWindow(ws[0]);
                    running.Add(new DockEntry { Kind = DockEntryKind.App, Key = key, Name = app.Name, App = app, IsPinned = false, IsActive = IsActive(ws), Windows = ws });
                }
                else
                {
                    var h = hiddenPinnedRunning.FirstOrDefault(x => x.App.Key == key);
                    if (h.App is null) continue;
                    running.Add(new DockEntry { Kind = DockEntryKind.App, Key = key, Name = h.App.Name, App = h.App, IsPinned = false, IsActive = IsActive(h.Windows), Windows = h.Windows });
                }
            }
            if (running.Count > 0)
            {
                if (pinnedAdded > 0) result.Add(new DockEntry { Kind = DockEntryKind.Separator, Key = SeparatorKey });
                result.AddRange(running);
            }
        }
        return result;
    }
}
