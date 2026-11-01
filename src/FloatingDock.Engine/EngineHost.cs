using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.Core.Taskbar;
using FloatingDock.Ipc;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.Taskbar;

namespace FloatingDock.Engine;

/// <summary>
/// The engine's composition root: creates every system-facing service once and exposes them over IPC.
/// Everything runs on the UI-loop thread unless noted; IPC handlers hop onto it where a service needs that.
/// </summary>
internal sealed partial class EngineHost : IDisposable
{
    public const int HotkeyLaunchpad = 1, HotkeyRestoreTaskbar = 2, HotkeyDockFocus = 3, HotkeySearch = 4;

    private readonly AppPaths _paths;
    private readonly UiLoop _ui;
    private readonly string[] _args;
    private readonly bool _diagnostic;
    private readonly MediaController _media = new();
    private SettingsStore _settings = null!;
    private AppListStore _pinned = null!;
    private AppListStore _tiles = null!;
    private IconPackService _packs = null!;
    private IconOverrideStore _overrides = null!;
    private IconResolver _resolver = null!;
    private IconProvider _icons = null!;
    private ShellWorker _worker = null!;
    private RunningAppsService _running = null!;
    private TaskbarController _taskbar = null!;
    private MessageWindow _msg = null!;
    private TrayIcon _tray = null!;
    private IpcServer _server = null!;
    private DockSettings _applied = new();
    private System.Threading.Timer? _settingsTimer, _taskbarTimer, _displayTimer, _packTimer;
    private FileSystemWatcher? _packWatcher;
    private bool _shuttingDown;
    private bool _dockUserHidden;
    private readonly Dictionary<int, bool> _hotkeyRegistered = new();

    public EngineHost(AppPaths paths, UiLoop ui, string[] args, bool diagnostic)
    {
        _paths = paths;
        _ui = ui;
        _args = args;
        _diagnostic = diagnostic;
    }

    public static string Version => typeof(EngineHost).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>Raised on the UI-loop thread when the engine should stop (quit command, tray exit).</summary>
    public event Action? QuitRequested;

    /// <summary>Must run on the UI-loop thread: the WinEvent hooks and the message window belong to it.</summary>
    public void Start()
    {
        _paths.EnsureCreated();
        EngineLog.Info($"engine {Version} starting, pid {Environment.ProcessId}");

        // A previous run may have been killed with the taskbar changed: put it back before anything is applied.
        _taskbar = new TaskbarController(_paths.TaskbarStateFile, m => EngineLog.Info(m));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _taskbar.Restore(); } catch (Exception) { } };
        ReportTaskbar(TaskbarRestoreFlow.Run(_taskbar, recoverOnly: true, interactive: !_diagnostic), "start-up recovery");

        bool firstRun = !File.Exists(_paths.PinnedFile);
        _settings = new SettingsStore(_paths.ConfigFile);
        _settings.SaveFailed += ex => EngineLog.Warn("settings could not be saved: " + ex.Message);
        // A diagnostic profile must never touch the user's desktop: leave the taskbar alone and do not autostart.
        if (_diagnostic) _settings.Update(x => { x.TaskbarMode = TaskbarMode.Leave; x.StartWithWindows = false; });
        _pinned = new AppListStore(_paths.PinnedFile);
        _tiles = new AppListStore(_paths.AppLibraryFile);
        _pinned.SaveFailed += ex => EngineLog.Warn("pinned apps could not be saved: " + ex.Message);
        _tiles.SaveFailed += ex => EngineLog.Warn("Launchpad tiles could not be saved: " + ex.Message);

        _packs = new IconPackService(_paths.IconPacksDir, Path.Combine(_paths.CacheDir, "tmp"));
        _packs.RemoveLegacyBundledPacks();
        if (_packs.Load(_settings.Current.ActiveIconPack) is null)
            _settings.Update(x => x.ActiveIconPack = IconPackService.DefaultPackId);
        _overrides = new IconOverrideStore(_paths.IconOverridesFile, _paths.UserIconsDir);
        _resolver = new IconResolver(_overrides, _packs);
        var s = _settings.Current;
        _resolver.Configure(s.UseIconPack, s.ActiveIconPack, s.PreferDarkVariants);

        _worker = new ShellWorker();
        _icons = new IconProvider(_resolver, _worker, new ShellIconExtractor(_paths.SystemIconCacheDir));
        _overrides.Changed += () => _ui.Post(_icons.Invalidate);
        _running = new RunningAppsService(_worker, _ui.Post);

        _msg = new MessageWindow();
        if (!_diagnostic)
        {
            _tray = new TrayIcon(_msg, "FloatingDock", Environment.ProcessPath);
            _tray.MenuProvider = BuildTrayMenu;
            _tray.MenuCommand += id => _ui.Post(() => OnTrayCommand(id));
            _tray.LeftClick += () => _ui.Post(() => UiCommand("toggleLaunchpad"));
        }
        _msg.HotkeyPressed += id => _ui.Post(() => OnHotkey(id));
        _msg.DisplayChanged += () => _ui.Post(ScheduleDisplayRefresh);
        _msg.WorkAreaOrSettingsChanged += () => _ui.Post(ScheduleDisplayRefresh);
        _msg.TaskbarCreated += () => _ui.Post(OnExplorerRestarted);
        _msg.SessionEnding += () => { try { _taskbar.Restore(); _settings.Flush(); } catch (Exception) { } };

        _server = new IpcServer(IpcEndpoint.SocketPathFor(_paths.Root), Version);
        _server.SessionOpened += session => EngineLog.Info($"client #{session.Id} connected");
        _server.SessionClosed += session => EngineLog.Info($"client #{session.Id} ({session.ClientName}) disconnected");
        RegisterHandlers(_server);
        _server.Start();

        _settings.Changed += _ => _ui.Post(ScheduleSettingsApply);
        _settings.Changed += snapshot => _server.Publish(Protocol.Events.SettingsChanged, SettingsNode(snapshot));
        _pinned.Changed += () => _server.Publish(Protocol.Events.PinnedChanged, PinnedNode());
        _tiles.Changed += () => _server.Publish("tiles.changed", null);
        _icons.Changed += () => _server.Publish("icons.changed", null);
        _running.Changed += (_, _) => _server.Publish(Protocol.Events.WindowsChanged, WindowsNode());
        _running.Start();

        _applied = s.Clone();
        ApplySystemSettings(null, s);
        _taskbarTimer = new System.Threading.Timer(_ => _ui.Post(MaintainTaskbar), null, Timeout.Infinite, Timeout.Infinite);
        if (s.TaskbarMode != TaskbarMode.Leave) _taskbarTimer.Change(750, 750);
        WatchIconPacks();

        if (firstRun) _ = SeedPinnedFromWindowsTaskbarAsync();
        if (!_diagnostic) _ = Task.Delay(600).ContinueWith(_ => _startPins.GetAsync()).Unwrap().ContinueWith(t => { if (t.IsFaulted) EngineLog.Warn("Start pins preload failed: " + t.Exception?.GetBaseException().Message); });
        EngineLog.Info($"engine ready on {IpcEndpoint.SocketPathFor(_paths.Root)} (profile {_paths.Root}, settings {_settings.LoadStatus})");
    }

    // ------------------------------------------------------------------ settings -> system

    /// <summary>Sliders fire many changes per second: apply at most once per ~30 ms, always with the latest values.</summary>
    private void ScheduleSettingsApply()
    {
        _settingsTimer ??= new System.Threading.Timer(_ => _ui.Post(() =>
        {
            if (_shuttingDown) return;
            var next = _settings.Current;
            var old = _applied;
            _applied = next.Clone();
            ApplySystemSettings(old, next);
        }), null, Timeout.Infinite, Timeout.Infinite);
        _settingsTimer.Change(30, Timeout.Infinite);
    }

    private void ApplySystemSettings(DockSettings? old, DockSettings s)
    {
        if (old is null || old.UseIconPack != s.UseIconPack || old.ActiveIconPack != s.ActiveIconPack || old.PreferDarkVariants != s.PreferDarkVariants)
        {
            _resolver.Configure(s.UseIconPack, s.ActiveIconPack, s.PreferDarkVariants);
            if (old is not null) _icons.Invalidate();
        }
        if (old is null || old.TaskbarMode != s.TaskbarMode) ApplyTaskbarMode(s.TaskbarMode, userChange: old is not null);
        if (_taskbarTimer is not null)
            _taskbarTimer.Change(s.TaskbarMode == TaskbarMode.Leave ? Timeout.Infinite : 750, s.TaskbarMode == TaskbarMode.Leave ? Timeout.Infinite : 750);
        if (!_diagnostic && (old is null || old.StartWithWindows != s.StartWithWindows || old.StartWithWindows != Autostart.IsEnabled()))
        {
            try { if (Environment.ProcessPath is { } exe) Autostart.Set(s.StartWithWindows, exe); }
            catch (Exception ex) { EngineLog.Warn("autostart failed: " + ex.Message); }
        }
        if (old is not null && old.LaunchpadIconStyle != s.LaunchpadIconStyle) _icons.Invalidate();
        if (old is null || old.SearchHotkey != s.SearchHotkey) RegisterHotkey(HotkeySearch, s.SearchHotkey);
        if (old is null || old.LaunchpadHotkey != s.LaunchpadHotkey) RegisterHotkey(HotkeyLaunchpad, s.LaunchpadHotkey);
        if (old is null || old.RestoreTaskbarHotkey != s.RestoreTaskbarHotkey) RegisterHotkey(HotkeyRestoreTaskbar, s.RestoreTaskbarHotkey);
        if (old is null || old.DockFocusHotkey != s.DockFocusHotkey) RegisterHotkey(HotkeyDockFocus, s.DockFocusHotkey);
    }

    private void RegisterHotkey(int id, string text)
    {
        if (_diagnostic) return;         // a test profile must not take the user's global shortcuts
        _msg.UnregisterHotkey(id);
        _hotkeyRegistered[id] = false;
        if (string.IsNullOrWhiteSpace(text)) { PublishHotkeyStatus(); return; }
        if (!HotkeyParser.TryParse(text, out var mods, out var vk)) { EngineLog.Warn($"invalid hotkey '{text}'"); PublishHotkeyStatus(); return; }
        bool ok = _msg.RegisterHotkey(id, mods, vk);
        _hotkeyRegistered[id] = ok;
        if (!ok) EngineLog.Warn($"hotkey '{text}' is already used by another program");
        PublishHotkeyStatus();
    }

    private void PublishHotkeyStatus() => _server?.Publish("hotkeys.changed", HotkeyStatusNode());

    private JsonObject HotkeyStatusNode()
    {
        bool Ok(int id) => !_hotkeyRegistered.TryGetValue(id, out var ok) || ok;
        return new JsonObject
        {
            ["launchpad"] = Ok(HotkeyLaunchpad), ["restoreTaskbar"] = Ok(HotkeyRestoreTaskbar),
            ["dockFocus"] = Ok(HotkeyDockFocus), ["search"] = Ok(HotkeySearch),
        };
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeySearch: UiCommand("toggleSearch"); break;
            case HotkeyLaunchpad: UiCommand("toggleLaunchpad"); break;
            case HotkeyRestoreTaskbar: RestoreTaskbarNow(); break;
            case HotkeyDockFocus: UiCommand("focusDock"); break;
        }
    }

    /// <summary>Tells the UI to do something that only it can show (open a window...).</summary>
    private void UiCommand(string name, JsonObject? extra = null)
    {
        var data = extra ?? new JsonObject();
        data["command"] = name;
        _server.Publish("ui.command", data);
    }

    // ------------------------------------------------------------------ system events

    private void ScheduleDisplayRefresh()
    {
        _displayTimer ??= new System.Threading.Timer(_ => _ui.Post(() => { if (!_shuttingDown) _server.Publish("display.changed", null); }),
            null, Timeout.Infinite, Timeout.Infinite);
        _displayTimer.Change(350, Timeout.Infinite);
    }

    private void OnExplorerRestarted()
    {
        EngineLog.Info("Explorer restarted (TaskbarCreated)");
        ApplyTaskbarMode(_settings.Current.TaskbarMode, userChange: false);
        ScheduleDisplayRefresh();
    }

    private void WatchIconPacks()
    {
        try
        {
            _packWatcher = new FileSystemWatcher(_packs.RootDirectory)
            {
                IncludeSubdirectories = true, EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => SchedulePackReload();
            _packWatcher.Created += changed; _packWatcher.Changed += changed; _packWatcher.Deleted += changed;
            _packWatcher.Renamed += (_, _) => SchedulePackReload();
        }
        catch (Exception ex) { EngineLog.Warn("icon pack watcher failed: " + ex.Message); }
    }

    private void SchedulePackReload()
    {
        _packTimer ??= new System.Threading.Timer(_ => _ui.Post(() => { _resolver.Reload(); _icons.Invalidate(); _server.Publish("packs.changed", null); }),
            null, Timeout.Infinite, Timeout.Infinite);
        _packTimer.Change(600, Timeout.Infinite);
    }

    private async Task SeedPinnedFromWindowsTaskbarAsync()
    {
        try
        {
            var pins = await _worker.Run(AppCatalog.ReadWindowsTaskbarPins);
            foreach (var p in pins) _pinned.Add(p);
        }
        catch (Exception ex) { EngineLog.Warn("taskbar pin import failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ taskbar

    private void ApplyTaskbarMode(TaskbarMode mode, bool userChange)
    {
        if (_diagnostic) return;         // a test profile never hides the user's taskbar, whatever its settings say
        TaskbarResult r;
        try { r = _taskbar.Apply(mode); }
        catch (Exception ex) { EngineLog.Error("taskbar apply failed", ex); r = new TaskbarResult(TaskbarOutcome.Refused, "Unexpected error: " + ex.Message); }
        ReportTaskbar(r, "apply " + mode);
        // The setting must not claim a mode that is not active: after a refused change fall back to Leave.
        if (r.Outcome == TaskbarOutcome.Refused && userChange && _settings.Current.TaskbarMode != TaskbarMode.Leave)
            _settings.Update(x => x.TaskbarMode = TaskbarMode.Leave);
    }

    private void MaintainTaskbar()
    {
        if (_diagnostic || _shuttingDown || _trayOperations > 0 || _settings.Current.TaskbarMode == TaskbarMode.Leave) return;
        var r = _taskbar.Maintain(_settings.Current.TaskbarMode);
        if (r.NeedsAttention) EngineLog.Warn("taskbar maintenance: " + r.Message);
    }

    /// <summary>Tray menu / hotkey / settings button: restore the Windows taskbar and stop hiding it.</summary>
    private void RestoreTaskbarNow()
    {
        TaskbarResult r;
        try { r = TaskbarRestoreFlow.Run(_taskbar, recoverOnly: false, interactive: !_diagnostic); }
        catch (Exception ex) { EngineLog.Error("restore failed", ex); r = new TaskbarResult(TaskbarOutcome.RetryNeeded, "Unexpected error: " + ex.Message); }
        ReportTaskbar(r, "manual restore", always: true);
        if (r.Success && _settings.Current.TaskbarMode != TaskbarMode.Leave) _settings.Update(x => x.TaskbarMode = TaskbarMode.Leave);
    }

    private void ReportTaskbar(TaskbarResult r, string what, bool always = false)
    {
        EngineLog.Info($"taskbar {what}: {r.Outcome} - {r.Message}");
        if (r.NeedsAttention || (always && r.Outcome == TaskbarOutcome.Ok))
            Notify("Windows taskbar", r.Message, error: !r.Success);
    }

    /// <summary>Greater than zero while the tray is being read or driven; the taskbar maintenance timer pauses meanwhile.</summary>
    private int _trayOperations;
    internal void TrayOperation(bool begin) { _trayOperations = Math.Max(0, _trayOperations + (begin ? 1 : -1)); }

    /// <summary>A balloon next to the tray icon: the only message channel when no window is open.</summary>
    private void Notify(string title, string text, bool error)
    {
        if (error) EngineLog.Warn($"{title}: {text}"); else EngineLog.Info($"{title}: {text}");
        try { _tray?.ShowBalloon(title, text, error); } catch (Exception) { /* no tray yet */ }
    }

    // ------------------------------------------------------------------ tray

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        string T(string text) => InterfaceText.Get(text, _settings.Current.Language);
        return
        [
            new(1, T("Launchpad")),
            new(2, T(_dockUserHidden ? "Show Dock" : "Hide Dock")),
            new(3, T("Settings…")),
            new(0, "", IsSeparator: true),
            new(4, T("Restore Windows taskbar")),
            new(5, T("Restart FloatingDock")),
            new(6, T("Exit")),
        ];
    }

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case 1: UiCommand("toggleLaunchpad"); break;
            case 2: SetDockUserHidden(!_dockUserHidden); break;
            case 3: UiCommand("settings"); break;
            case 4: RestoreTaskbarNow(); break;
            case 5: Restart(); break;
            case 6: Shutdown(); break;
        }
    }

    private void SetDockUserHidden(bool hidden)
    {
        _dockUserHidden = hidden;
        _server.Publish("dock.hiddenChanged", new JsonObject { ["hidden"] = hidden });
    }

    private void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var args = new List<string> { "--wait-for-exit", Environment.ProcessId.ToString() };
                args.AddRange(_args.Where(a => a != "--quit"));
                var info = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var a in args) info.ArgumentList.Add(a);
                Process.Start(info);
            }
        }
        catch (Exception ex) { EngineLog.Error("restart failed", ex); return; }
        Shutdown();
    }

    private void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try { _taskbar.Restore(); } catch (Exception ex) { EngineLog.Warn("taskbar restore on exit failed: " + ex.Message); }
        _server.Publish("engine.stopping", null);
        QuitRequested?.Invoke();
    }

    public void Dispose()
    {
        _shuttingDown = true;
        _settingsTimer?.Dispose(); _taskbarTimer?.Dispose(); _displayTimer?.Dispose(); _packTimer?.Dispose();
        _packWatcher?.Dispose();
        _running?.Dispose();
        _tray?.Dispose();
        _msg?.Dispose();
        _server?.Dispose();
        _worker?.Dispose();
        _settings?.Dispose();
        EngineLog.Info("engine stopped");
    }

    // ------------------------------------------------------------------ JSON views

    private static JsonNode SettingsNode(DockSettings s) => JsonSerializer.SerializeToNode(s, JsonFile.Options)!;

    private static JsonObject AppNode(AppRef app)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(app, JsonFile.Options)!;
        node["Key"] = app.Key;
        return node;
    }

    private static JsonArray AppsNode(IEnumerable<AppRef> apps)
    {
        var list = new JsonArray();
        foreach (var a in apps) list.Add(AppNode(a));
        return list;
    }

    private JsonNode PinnedNode() => AppsNode(_pinned.Snapshot());

    /// <summary>Must run on the UI-loop thread (the running-apps service is not thread-safe).</summary>
    private JsonObject WindowsNode()
    {
        var windows = new JsonArray();
        foreach (var w in _running.Snapshot())
            windows.Add(new JsonObject
            {
                ["hwnd"] = w.Hwnd, ["pid"] = w.ProcessId, ["exePath"] = w.ExePath, ["aumid"] = w.Aumid,
                ["title"] = w.Title, ["className"] = w.ClassName, ["minimized"] = w.IsMinimized,
            });
        return new JsonObject { ["foreground"] = _running.ForegroundRoot, ["windows"] = windows };
    }

    private static JsonNode? MediaNode(MediaState? m) => m is null ? null : new JsonObject
    {
        ["title"] = m.Title, ["artist"] = m.Artist, ["source"] = m.Source, ["canPrevious"] = m.Previous, ["canToggle"] = m.Toggle,
        ["canNext"] = m.Next, ["playing"] = m.Playing, ["position"] = m.Position, ["duration"] = m.Duration,
        ["canShuffle"] = m.CanShuffle, ["shuffle"] = m.Shuffle, ["canRepeat"] = m.CanRepeat, ["repeat"] = m.Repeat,
    };
}
