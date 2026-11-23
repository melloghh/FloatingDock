using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.Ipc;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Engine;

/// <summary>The dock's model: which slots exist (Start, Launchpad, pinned, running), and what a click on one does.</summary>
internal sealed partial class EngineHost
{
    private readonly DockEntryReconciler _reconciler = new();
    private IReadOnlyList<DockEntry> _dockEntries = Array.Empty<DockEntry>();
    private string _dockEntriesShape = "";
    private System.Threading.Timer? _dockTimer;
    private readonly HashSet<string> _activating = new();

    private static readonly AppRef StartApp = new() { Id = "special-start", Name = "Start", Aliases = ["Windows", "Start Menu", "Windows Start", "Пуск"] };
    private static readonly AppRef LaunchpadApp = new() { Id = "special-launchpad", Name = "Launchpad", Aliases = ["Apps", "App Library", "All Apps", "Launcher", "Приложения"] };

    private void RegisterDockHandlers(IpcServer server)
    {
        _running.Changed += (_, _) => ScheduleDockEntries();
        _pinned.Changed += ScheduleDockEntries;
        _settings.Changed += _ => ScheduleDockEntries();

        server.Register("dock.entries", async _ => await _ui.InvokeAsync(() => { RebuildDockEntries(publish: false); return (JsonNode?)DockEntriesNode(); }));

        server.Register("dock.activate", async request =>
        {
            var key = RequireString(request, "key");
            bool newInstance = request.Params["newInstance"]?.GetValue<bool>() ?? false;
            return await _ui.InvokeAsync(() => ActivateEntry(key, newInstance)).Unwrap();
        });
        server.RegisterSync("dock.close", request =>
        {
            var entry = FindEntry(RequireString(request, "key"));
            if (entry is not null) WindowActivator.RequestClose(entry.Windows);
            return null;
        });
        server.RegisterSync("dock.menuInfo", request =>
        {
            var key = RequireString(request, "key");
            var entry = FindEntry(key);
            var pinned = _pinned.Snapshot().ToList();
            int index = pinned.FindIndex(p => p.Key == key);
            var app = entry?.App;
            return new JsonObject
            {
                ["found"] = entry is not null, ["pinned"] = entry?.IsPinned ?? false, ["running"] = entry?.IsRunning ?? false,
                ["windows"] = entry?.Windows.Count ?? 0, ["hasLocation"] = app is not null && ShellLauncher.LocationOf(app) is not null,
                ["hasOverride"] = app is not null && _overrides.Has(app), ["pinnedIndex"] = index, ["pinnedCount"] = pinned.Count,
            };
        });
        server.RegisterSync("pinned.addSeparator", request =>
        {
            var after = request.Params["afterKey"]?.GetValue<string>();
            var pins = _pinned.Snapshot().ToList();
            int at = after is null ? -1 : pins.FindIndex(p => p.Key == after);
            _pinned.Add(AppRef.Separator(), at >= 0 ? at + 1 : pins.Count);
            return null;
        });
        server.RegisterSync("windows.close", request =>
        {
            var hwnd = request.Params["hwnd"]?.GetValue<long>() ?? throw BadParams("hwnd is required.");
            NativeMethods.PostMessage(new IntPtr(hwnd), 0x0010, IntPtr.Zero, IntPtr.Zero);
            return null;
        });
        server.RegisterSync("windows.rect", request =>
        {
            var hwnd = new IntPtr(request.Params["hwnd"]?.GetValue<long>() ?? throw BadParams("hwnd is required."));
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return null;
            return new JsonObject { ["w"] = r.Right - r.Left, ["h"] = r.Bottom - r.Top, ["minimized"] = NativeMethods.IsIconic(hwnd) };
        });

        // Where the dock's icons are on screen: the shell uses it as the target of minimize / restore animations.
        server.RegisterSync("dock.anchors", request =>
        {
            var anchors = new List<(string Key, RECT Rect)>();
            foreach (var a in (request.Params["anchors"] as JsonArray) ?? [])
                if (a is JsonObject o)
                    anchors.Add((o["key"]!.GetValue<string>(), new RECT { Left = o["left"]!.GetValue<int>(), Top = o["top"]!.GetValue<int>(), Right = o["right"]!.GetValue<int>(), Bottom = o["bottom"]!.GetValue<int>() }));
            RECT? fallback = request.Params["fallback"] is JsonObject f
                ? new RECT { Left = f["left"]!.GetValue<int>(), Top = f["top"]!.GetValue<int>(), Right = f["right"]!.GetValue<int>(), Bottom = f["bottom"]!.GetValue<int>() }
                : null;
            var map = new Dictionary<long, RECT>();
            foreach (var (key, rect) in anchors)
                if (_dockEntries.FirstOrDefault(e => e.Key == key) is { } entry)
                    foreach (var w in entry.Windows) map[w.Hwnd] = rect;
            Volatile.Write(ref _anchorMap, new AnchorSnapshot(map, anchors.ToArray(), fallback));
            _msg.AnimationRectProvider ??= ShellAnimationRect;
            return null;
        });
    }

    private sealed record AnchorSnapshot(Dictionary<long, RECT> ByWindow, (string Key, RECT Rect)[] Anchors, RECT? Fallback);
    private AnchorSnapshot _anchorMap = new(new Dictionary<long, RECT>(), Array.Empty<(string, RECT)>(), null);

    /// <summary>Called on the shell thread: the target rectangle of a window's minimize / restore animation.</summary>
    private RECT? ShellAnimationRect(IntPtr window)
    {
        var (byWindow, anchors, fallback) = Volatile.Read(ref _anchorMap);
        var root = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOTOWNER);
        if (root != IntPtr.Zero) window = root;
        if (byWindow.TryGetValue(window.ToInt64(), out var exact)) return exact;
        if (!NativeMethods.IsWindow(window)) return null;
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        string? exe = WindowIdentityResolver.GetExePath(pid);
        string? aumid = AppMatching.IsHostExe(exe) ? WindowIdentityResolver.GetProcessAumid(pid) : null;
        var info = new WindowInfo(window.ToInt64(), (int)pid, exe, aumid, null, null, NativeMethods.IsIconic(window));
        var entries = Volatile.Read(ref _dockEntries);
        foreach (var (key, rect) in anchors)
            if (entries.FirstOrDefault(e => e.Key == key)?.App is { } app && AppMatching.Matches(app, info)) return rect;
        if (IsUiProcess((int)pid) || exe is not null && WindowFilter.IsIgnoredExe(exe)) return null;
        return fallback;
    }

    private DockEntry? FindEntry(string key) => Volatile.Read(ref _dockEntries).FirstOrDefault(e => e.Key == key);

    private void ScheduleDockEntries()
    {
        if (_shuttingDown) return;
        (_dockTimer ??= new System.Threading.Timer(_ => _ui.Post(() => RebuildDockEntries(publish: true)), null, Timeout.Infinite, Timeout.Infinite)).Change(25, Timeout.Infinite);
    }

    private void RebuildDockEntries(bool publish)
    {
        if (_shuttingDown) return;
        var s = _settings.Current;
        var entries = _reconciler.Build(new DockEntryOptions(s.ShowStartButton, s.ShowLaunchpadButton, s.ShowPinned, s.ShowRunning),
            _pinned.Snapshot(), _running.Snapshot(), _running.ForegroundRoot);
        Volatile.Write(ref _dockEntries, entries);
        // Window titles change all the time (a counter in a title, a playing track) and the dock draws none of them: only a change
        // in what the dock shows is published. Previews ask for the current titles when they open (dock.entries).
        var shape = DockEntriesNode(withTitles: false).ToJsonString();
        if (shape == _dockEntriesShape) return;
        _dockEntriesShape = shape;
        if (publish) _server.Publish("dock.entries", DockEntriesNode());
    }

    private JsonArray DockEntriesNode(bool withTitles = true)
    {
        var list = new JsonArray();
        foreach (var e in Volatile.Read(ref _dockEntries))
        {
            var windows = new JsonArray();
            foreach (var w in e.Windows)
                windows.Add(new JsonObject { ["hwnd"] = w.Hwnd, ["title"] = withTitles ? w.Title : "", ["minimized"] = w.IsMinimized });
            var app = e.Kind switch { DockEntryKind.Start => StartApp, DockEntryKind.Launchpad => LaunchpadApp, _ => e.App };
            list.Add(new JsonObject
            {
                ["key"] = e.Key, ["kind"] = e.Kind.ToString(), ["name"] = e.Name, ["pinned"] = e.IsPinned, ["running"] = e.IsRunning,
                ["active"] = e.IsActive, ["app"] = app is null ? null : AppNode(app), ["windows"] = windows,
            });
        }
        return list;
    }

    /// <summary>A click on a dock slot. Running apps are activated (or minimized when already in front); others are launched.</summary>
    private async Task<JsonNode?> ActivateEntry(string key, bool newInstance)
    {
        var entry = FindEntry(key) ?? throw BadParams("That dock item is no longer there.");
        switch (entry.Kind)
        {
            case DockEntryKind.Start:
                StartButton.ToggleStartMenu();
                break;
            case DockEntryKind.Launchpad:
                UiCommand("toggleLaunchpad");
                break;
            case DockEntryKind.App when entry.IsRunning && !newInstance:
                if (!_activating.Add(key)) break;
                try
                {
                    var result = await WindowActivator.ActivateOrMinimizeAsync(entry.Windows, _settings.Current.MinimizeOnRepeatClick);
                    if (result == WindowActivator.ActivationResult.NoLiveWindow)
                    {
                        _running.FullScan();
                        if (entry.App is { } app) _ = _worker.Run(() => ShellLauncher.Launch(app, false));
                    }
                    else if (result == WindowActivator.ActivationResult.Denied)
                        Notify("Could not switch to " + entry.Name, "Windows did not give this window focus. Check for an open dialog or a different virtual desktop.", true);
                }
                finally { _activating.Remove(key); }
                break;
            case DockEntryKind.App when entry.App is { } launch:
                _ = _worker.Run(() =>
                {
                    if (!ShellLauncher.Launch(launch, newInstance)) { EngineLog.Warn("launch failed: " + launch.Name); Notify("Could not open " + launch.Name, "The shortcut or application is unavailable.", true); }
                });
                break;
        }
        return null;
    }
}
