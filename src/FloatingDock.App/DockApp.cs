using System.Diagnostics;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.UI.Dock;
using FloatingDock.UI.Launchpad;
using FloatingDock.UI.Services;
using FloatingDock.UI.Settings;
using FloatingDock.Core.Taskbar;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.Taskbar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI;

/// <summary>Composition root: creates every service once and wires them together. Everything here runs on the UI thread unless noted.</summary>
public sealed partial class DockApp : Application
{
    public const int HotkeyLaunchpad = 1, HotkeyRestoreTaskbar = 2, HotkeyDockFocus = 3, HotkeySearch = 4;

    private static DockApp? _current;
    private static TaskbarController? _emergencyTaskbar;
    private static Logger? _emergencyLog;

    private readonly SingleInstance _single;
    private readonly string[] _args;
    private DispatcherQueueTimer? _settingsTimer;
    private DockSettings _applied = new();
    private DispatcherQueueTimer? _displayTimer, _packReloadTimer;
    private DispatcherQueueTimer? _taskbarTimer;
    private FileSystemWatcher? _packWatcher;
    private bool _shuttingDown;

    internal static new DockApp Current => _current ?? throw new InvalidOperationException("App not started");

    internal DispatcherQueue UiQueue { get; }
    internal AppPaths Paths { get; private set; } = null!;
    internal Logger Log { get; private set; } = null!;
    internal SettingsStore Settings { get; private set; } = null!;
    internal AppListStore Pinned { get; private set; } = null!;
    internal AppListStore UserTiles { get; private set; } = null!;
    internal IconOverrideStore Overrides { get; private set; } = null!;
    internal IconPackService Packs { get; private set; } = null!;
    internal IconResolver Resolver { get; private set; } = null!;
    internal ShellWorker Worker { get; private set; } = null!;
    private ShellWorker? _trayWorker;
    private int _trayOperations;
    internal StartPinsService StartPins { get; private set; } = null!;
    internal IconService Icons { get; private set; } = null!;
    internal RunningAppsService Running { get; private set; } = null!;
    internal TaskbarController Taskbar { get; private set; } = null!;
    internal MessageWindow Msg { get; private set; } = null!;
    internal TilingManager Tiling { get; private set; } = null!;
    internal TrayIcon Tray { get; private set; } = null!;
    internal DockHost Dock { get; private set; } = null!;
    internal MenuBar.MenuBarHost? TopBar { get; private set; }
    internal Search.CommandPaletteWindow? Palette { get; private set; }
    internal LaunchpadWindow? LaunchpadWin { get; private set; }
    internal SettingsWindow? SettingsWin { get; private set; }
    internal IntPtr MessageHandle => Msg.Handle;
    internal Dictionary<int, bool> HotkeyRegistered { get; } = new();

    public DockApp(SingleInstance single, string[] args)
    {
        _single = single;
        _args = args;
        _current = this;
        UiQueue = DispatcherQueue.GetForCurrentThread();
        Program.TraceStartup("loading XAML control resources");
        InitializeComponent();
        Program.TraceStartup("XAML control resources loaded");
        UnhandledException += (_, e) =>
        {
            Program.UiFailureCount++;
            TryLog("XAML unhandled exception", e.Exception);
            e.Handled = true;           // a UI glitch must not kill the dock (and the taskbar state is restored on a real crash)
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { Initialize(_args); }
        catch (Exception ex) { Program.ReportStartupError(ex); EmergencyRestore(ex); Environment.Exit(1); }
    }

    // ------------------------------------------------------------------ startup

    private void Initialize(string[] args)
    {
        Paths = Program.DataPaths;
        Paths.EnsureCreated();
        Log = new Logger(Paths.LogsDir);
        _emergencyLog = Log;
        Log.Info($"FloatingDock {typeof(DockApp).Assembly.GetName().Version} starting, pid {Environment.ProcessId}");

        // a previous run may have crashed / been killed with the taskbar changed: put it back BEFORE anything is applied
        Taskbar = new TaskbarController(Paths.TaskbarStateFile, m => Log.Info(m));
        _emergencyTaskbar = Taskbar;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Taskbar.Restore(); } catch { } };      // Environment.Exit paths; a forced kill cannot run this (the record on disk covers it)
        ReportTaskbar(TaskbarRestoreFlow.Run(Taskbar, recoverOnly: true, interactive: true), "start-up recovery");

        bool firstRun = !File.Exists(Paths.PinnedFile);
        Settings = new SettingsStore(Paths.ConfigFile);
        if (Program.IsSmokeTest) Settings.Update(x => { x.AnimationFps = Program.TestAnimationFps; x.HideOnFullscreen = false; if (!Program.TestDefaultSettings) x.TaskbarMode = TaskbarMode.Leave; x.StartWithWindows = false; });
        Settings.SaveFailed += ex => UiQueue.TryEnqueue(() => Notify("Settings were not saved", "FloatingDock will keep trying. " + ex.Message, true));
        // the installer's "Start with Windows" task: the app owns this setting, so the installer just asks for it
        if (args.Any(a => a.Equals("--enable-autostart", StringComparison.OrdinalIgnoreCase)) && !Settings.Current.StartWithWindows) Settings.Update(x => x.StartWithWindows = true);
        Pinned = new AppListStore(Paths.PinnedFile);
        UserTiles = new AppListStore(Paths.AppLibraryFile);
        Pinned.SaveFailed += ex => UiQueue.TryEnqueue(() => Notify("Pinned apps were not saved", "The change was undone because the file could not be written: " + ex.Message, true));
        UserTiles.SaveFailed += ex => UiQueue.TryEnqueue(() => Notify("Launchpad tiles were not saved", "The change was undone because the file could not be written: " + ex.Message, true));
        if (Pinned.LoadStatus == LoadStatus.Corrupt) Log.Warn("pinned.json was damaged; a copy was kept as pinned.json.corrupt");
        if (UserTiles.LoadStatus == LoadStatus.Corrupt) Log.Warn("library.json was damaged; a copy was kept as library.json.corrupt");

        Packs = new IconPackService(Paths.IconPacksDir, Path.Combine(Paths.CacheDir, "tmp"));
        Packs.RemoveLegacyBundledPacks();
        if (Packs.Load(Settings.Current.ActiveIconPack) is null)
            Settings.Update(x => x.ActiveIconPack = IconPackService.DefaultPackId);
        Overrides = new IconOverrideStore(Paths.IconOverridesFile, Paths.UserIconsDir);
        if (Overrides.LoadStatus == LoadStatus.Corrupt) Log.Warn("icon-overrides.json was damaged; a copy was kept as icon-overrides.json.corrupt");
        Resolver = new IconResolver(Overrides, Packs);
        var s = Settings.Current;
        Resolver.Configure(s.UseIconPack, s.ActiveIconPack, s.PreferDarkVariants);

        Worker = new ShellWorker();
        StartPins = new StartPinsService(Worker,Path.Combine(Paths.CacheDir,"start-pins.json"));
        Icons = new IconService(Resolver, Worker, new ShellIconExtractor(Paths.SystemIconCacheDir));
        Overrides.Changed += () => UiQueue.TryEnqueue(Icons.Invalidate);

        Running = new RunningAppsService(Worker, a => UiQueue.TryEnqueue(() => a()));

        Msg = new MessageWindow();
        Tray = new TrayIcon(Msg, "FloatingDock", Environment.ProcessPath);
        Tray.MenuProvider = BuildTrayMenu;
        Tray.MenuCommand += OnTrayCommand;
        Tray.LeftClick += () => UiQueue.TryEnqueue(ToggleLaunchpad);
        Msg.HotkeyPressed += id => UiQueue.TryEnqueue(() => OnHotkey(id));
        Msg.DisplayChanged += () => UiQueue.TryEnqueue(ScheduleDisplayRefresh);
        Msg.WorkAreaOrSettingsChanged += () => UiQueue.TryEnqueue(ScheduleDisplayRefresh);
        Msg.TaskbarCreated += () => UiQueue.TryEnqueue(OnExplorerRestarted);
        Msg.SessionEnding += () => { try { Tiling?.EmergencyRestore();Taskbar.Restore(); Settings.Flush(); } catch { /* the session is ending; the next start recovers from the record */ } };

        _single.ListenForSignals(() => UiQueue.TryEnqueue(() => ShowSettings()), () => UiQueue.TryEnqueue(Shutdown));

        Dock = new DockHost(this);
        Tiling=new TilingManager(this);
        TopBar = new MenuBar.MenuBarHost(this);
        Running.Start();
        Dock.Start();

        _applied = s.Clone();
        ApplySystemSettings(null, s);
        _taskbarTimer = UiQueue.CreateTimer();
        _taskbarTimer.IsRepeating = true;
        _taskbarTimer.Interval = TimeSpan.FromMilliseconds(750);
        _taskbarTimer.Tick += (_, _) =>
        {
            if (_shuttingDown || Settings.Current.TaskbarMode == TaskbarMode.Leave) return;
            var result = Taskbar.Maintain(Settings.Current.TaskbarMode);
            if (result.NeedsAttention) Log.Warn("taskbar maintenance: " + result.Message);
        };
        if (s.TaskbarMode != TaskbarMode.Leave) _taskbarTimer.Start();
        Settings.Changed += _ => UiQueue.TryEnqueue(ScheduleSettingsApply);
        WatchIconPacks();

        if (firstRun) _ = SeedPinnedFromWindowsTaskbarAsync();
        if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase))) ShowSettings();
        Log.Info("started");
        // Populate the shared catalogue after the dock's first paint. Opening does
        // not wait for this work or for a batch of shell icon extractions.
        if(!Program.IsSmokeTest)_ = WarmStartPinsAsync();
        if (Program.IsSmokeTest) UiQueue.TryEnqueue(async () => await RuntimeSmoke.RunAsync(this));
    }

    private async Task SeedPinnedFromWindowsTaskbarAsync()
    {
        try
        {
            var pins = await Worker.Run(AppCatalog.ReadWindowsTaskbarPins);
            foreach (var p in pins) Pinned.Add(p);
        }
        catch (Exception ex) { Log.Warn("taskbar pin import failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ settings → system

    /// <summary>Sliders fire many changes per second: apply at most once per ~30 ms, always with the latest values.</summary>
    private void ScheduleSettingsApply()
    {
        if (_settingsTimer is null)
        {
            _settingsTimer = UiQueue.CreateTimer();
            _settingsTimer.IsRepeating = false;
            _settingsTimer.Interval = TimeSpan.FromMilliseconds(30);
            _settingsTimer.Tick += (_, _) => OnSettingsChanged(Settings.Current);
        }
        if (!_settingsTimer.IsRunning) _settingsTimer.Start();
    }

    private void OnSettingsChanged(DockSettings ns)
    {
        if (_shuttingDown) return;
        var old = _applied;
        _applied = ns.Clone();
        ApplySystemSettings(old, ns);
        Dock.ApplySettings();
        TopBar?.ApplySettings();
    }

    private void ApplySystemSettings(DockSettings? old, DockSettings s)
    {
        Tiling.Apply(s);
        if (old is null || old.UseIconPack != s.UseIconPack || old.ActiveIconPack != s.ActiveIconPack || old.PreferDarkVariants != s.PreferDarkVariants)
        {
            Resolver.Configure(s.UseIconPack, s.ActiveIconPack, s.PreferDarkVariants);
            if (old is not null) Icons.Invalidate();
        }
        if (old is null || old.TaskbarMode != s.TaskbarMode) ApplyTaskbarMode(s.TaskbarMode, userChange: old is not null);
        if (_taskbarTimer is not null)
        {
            if (s.TaskbarMode == TaskbarMode.Leave) _taskbarTimer.Stop();
            else if (!_taskbarTimer.IsRunning) _taskbarTimer.Start();
        }
        if (!Program.IsSmokeTest && !Program.IsDiagnosticProfile && (old is null || old.StartWithWindows != s.StartWithWindows || old.StartWithWindows != Autostart.IsEnabled()))
        {
            try { if (Environment.ProcessPath is { } exe) Autostart.Set(s.StartWithWindows, exe); } catch (Exception ex) { Log.Warn("autostart failed: " + ex.Message); }
        }
        if(old is not null && old.LaunchpadIconStyle!=s.LaunchpadIconStyle)Icons.Invalidate();
        if(old is null || old.SearchHotkey!=s.SearchHotkey)RegisterHotkey(HotkeySearch,s.SearchHotkey);
        if (old is null || old.LaunchpadHotkey != s.LaunchpadHotkey) RegisterHotkey(HotkeyLaunchpad, s.LaunchpadHotkey);
        if (old is null || old.RestoreTaskbarHotkey != s.RestoreTaskbarHotkey) RegisterHotkey(HotkeyRestoreTaskbar, s.RestoreTaskbarHotkey);
        if (old is null || old.DockFocusHotkey != s.DockFocusHotkey) RegisterHotkey(HotkeyDockFocus, s.DockFocusHotkey);
    }

    private void RegisterHotkey(int id, string text)
    {
        Msg.UnregisterHotkey(id);
        HotkeyRegistered[id] = false;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!HotkeyParser.TryParse(text, out var mods, out var vk)) { Log.Warn($"invalid hotkey '{text}'"); return; }
        bool ok = Msg.RegisterHotkey(id, mods, vk);
        HotkeyRegistered[id] = ok;
        if (!ok) Log.Warn($"hotkey '{text}' is already used by another program");
    }

    private void OnHotkey(int id)
    {
        if(Tiling.HandleHotkey(id))return;
        switch (id)
        {
            case HotkeySearch: ToggleSearch(); break;
            case HotkeyLaunchpad: ToggleLaunchpad(); break;
            case HotkeyRestoreTaskbar: RestoreTaskbarNow(); break;
            case HotkeyDockFocus: Dock.FocusDock(); break;
        }
    }

    // ------------------------------------------------------------------ system events

    private void ScheduleDisplayRefresh()
    {
        if (_displayTimer is null)
        {
            _displayTimer = UiQueue.CreateTimer();
            _displayTimer.IsRepeating = false;
            _displayTimer.Interval = TimeSpan.FromMilliseconds(350);
            _displayTimer.Tick += (_, _) => { if (!_shuttingDown) { Dock.RefreshMonitors(); TopBar?.ApplySettings(); } };
        }
        _displayTimer.Stop();
        _displayTimer.Start();
    }

    private void OnExplorerRestarted()
    {
        Log.Info("Explorer restarted (TaskbarCreated)");
        Dock.OnExplorerRestarted();
        ApplyTaskbarMode(Settings.Current.TaskbarMode, userChange: false);
        ScheduleDisplayRefresh();
    }

    private void WatchIconPacks()
    {
        try
        {
            _packWatcher = new FileSystemWatcher(Packs.RootDirectory) { IncludeSubdirectories = true, EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler h = (_, _) => UiQueue.TryEnqueue(SchedulePackReload);
            _packWatcher.Created += h; _packWatcher.Changed += h; _packWatcher.Deleted += h;
            _packWatcher.Renamed += (_, _) => UiQueue.TryEnqueue(SchedulePackReload);
        }
        catch (Exception ex) { Log.Warn("icon pack watcher failed: " + ex.Message); }
    }

    private void SchedulePackReload()
    {
        if (_packReloadTimer is null)
        {
            _packReloadTimer = UiQueue.CreateTimer();
            _packReloadTimer.IsRepeating = false;
            _packReloadTimer.Interval = TimeSpan.FromMilliseconds(600);
            _packReloadTimer.Tick += (_, _) => { Resolver.Reload(); Icons.Invalidate(); };
        }
        _packReloadTimer.Stop();
        _packReloadTimer.Start();
    }

    // ------------------------------------------------------------------ commands

    internal async Task<IReadOnlyList<TrayItem>> ReadTrayItemsAsync()
    {
        _trayOperations++;_taskbarTimer?.Stop();
        try{return await (_trayWorker??=new ShellWorker()).Run(SystemTrayOverflow.ReadItems);}
        catch(Exception ex){Log.Warn("Tray icons unavailable: "+ex.Message);return Array.Empty<TrayItem>();}
        finally{if(--_trayOperations==0&&!_shuttingDown&&Settings.Current.TaskbarMode!=TaskbarMode.Leave)_taskbarTimer?.Start();}
    }
    internal async Task<bool> ActivateTrayItemAsync(TrayItem item,bool context,FloatingDock.Windows.Native.POINT? anchor=null)
    {
        _trayOperations++;_taskbarTimer?.Stop();
        try{return await (_trayWorker??=new ShellWorker()).Run(()=>SystemTrayOverflow.ActivateItem(item,context,anchor));}
        catch(Exception ex){Log.Warn("Tray action failed: "+ex.Message);return false;}
        finally{if(--_trayOperations==0&&!_shuttingDown&&Settings.Current.TaskbarMode!=TaskbarMode.Leave)_taskbarTimer?.Start();}
    }
    internal void ToggleSearch() { Palette ??=new Search.CommandPaletteWindow();Palette.Toggle(); }

    internal void ToggleLaunchpad()
    {
        try
        {
            LaunchpadWin ??= new LaunchpadWindow();
            LaunchpadWin.Toggle();
        }
        catch (Exception ex) { Log.Error("Launchpad preparation failed", ex); }
    }

    private async Task WarmStartPinsAsync()
    {
        await Task.Delay(600);
        if(_shuttingDown)return;
        try { await StartPins.GetAsync(); }
        catch(Exception ex) { Log.Warn("Optional Start pins preload failed: "+ex.Message); }
    }

    internal Task PrepareLaunchpadAsync()
    {
        LaunchpadWin ??= new LaunchpadWindow();
        return LaunchpadWin.PrepareAsync();
    }

    internal void ShowSettings(string? section = null)
    {
        if (SettingsWin is null)
        {
            SettingsWin = new SettingsWindow();
            SettingsWin.Closed += (_, _) => SettingsWin = null;
        }
        SettingsWin.ShowSection(section);
    }

    /// <summary>Applies the taskbar mode and tells the user when it could not be done safely. A refused change leaves the taskbar untouched.</summary>
    private void ApplyTaskbarMode(TaskbarMode mode, bool userChange)
    {
        TaskbarResult r;
        try { r = Taskbar.Apply(mode); }
        catch (Exception ex) { Log.Error("taskbar apply failed", ex); r = new TaskbarResult(TaskbarOutcome.Refused, "Unexpected error: " + ex.Message); }
        ReportTaskbar(r, "apply " + mode);
        // the setting must not claim a mode that is not active: after a refused change fall back to Leave (nothing was changed, nothing to undo)
        if (r.Outcome == TaskbarOutcome.Refused && userChange && Settings.Current.TaskbarMode != TaskbarMode.Leave)
            Settings.Update(x => x.TaskbarMode = TaskbarMode.Leave);
    }

    /// <summary>Tray menu / hotkey / settings button: restore the Windows taskbar and stop hiding it. Never silent when the original state is unknown.</summary>
    internal void RestoreTaskbarNow()
    {
        TaskbarResult r;
        try { r = TaskbarRestoreFlow.Run(Taskbar, recoverOnly: false, interactive: true); }
        catch (Exception ex) { Log.Error("restore failed", ex); r = new TaskbarResult(TaskbarOutcome.RetryNeeded, "Unexpected error: " + ex.Message); }
        ReportTaskbar(r, "manual restore", always: true);
        if (r.Success && Settings.Current.TaskbarMode != TaskbarMode.Leave) Settings.Update(s => s.TaskbarMode = TaskbarMode.Leave);
    }

    /// <summary>Removes a custom icon; when the change cannot be saved the icon stays and the user is told.</summary>
    internal void TryResetIcon(FloatingDock.Core.Models.AppRef app)
    {
        try { Overrides.ResetForApp(app); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("Icon not reset", "The change could not be saved: " + ex.Message, true); }
    }

    internal void TryResetIcon(string appKey)
    {
        try { Overrides.Reset(appKey); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("Icon not reset", "The change could not be saved: " + ex.Message, true); }
    }

    private void ReportTaskbar(TaskbarResult r, string what, bool always = false)
    {
        Log.Info($"taskbar {what}: {r.Outcome} - {r.Message}");
        if (r.NeedsAttention || (always && r.Outcome == TaskbarOutcome.Ok))
            Notify("Windows taskbar", r.Message, error: !r.Success);
    }

    /// <summary>User-visible message (tray balloon) + log. Safe to call from the UI thread at any time after the tray icon exists.</summary>
    internal void Notify(string title, string text, bool error)
    {
        if (error) Log.Warn($"{title}: {text}"); else Log.Info($"{title}: {text}");
        try { Tray?.ShowBalloon(title, text, error); } catch { /* no tray yet */ }
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu() => new TrayMenuItem[]
    {
        new(1, "Launchpad"),
        new(2, Dock.UserHidden ? "Show Dock" : "Hide Dock"),
        new(3, "Settings…"),
        new(0, "", IsSeparator: true),
        new(4, "Restore Windows taskbar"),
        new(5, "Restart FloatingDock"),
        new(6, "Exit"),
    };

    private void OnTrayCommand(int id) => UiQueue.TryEnqueue(() =>
    {
        switch (id)
        {
            case 1: ToggleLaunchpad(); break;
            case 2: Dock.SetUserHidden(!Dock.UserHidden); break;
            case 3: ShowSettings(); break;
            case 4: RestoreTaskbarNow(); break;
            case 5: Restart(); break;
            case 6: Shutdown(); break;
        }
    });

    internal void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) Process.Start(new ProcessStartInfo(exe, $"--wait-for-exit {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error("restart failed", ex); return; }
        Shutdown();
    }

    /// <summary>Orderly exit: the Windows taskbar is always restored first.</summary>
    internal void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _settingsTimer?.Stop();
        _displayTimer?.Stop();
        _packReloadTimer?.Stop();
        _taskbarTimer?.Stop();
        Log.Info("shutting down");
        try
        {
            var r = Taskbar.Restore();           // restores only what FloatingDock changed; nothing when the mode was Leave
            Log.Info($"taskbar restore on exit: {r.Outcome} - {r.Message}");
            if (r.Outcome is TaskbarOutcome.RetryNeeded or TaskbarOutcome.NeedsConfirmation)
                NativeDialogs.Error(r.Message + "\n\nRun \"FloatingDock.exe --restore-taskbar\" (or press the emergency hotkey after starting FloatingDock again) to restore it.");
        }
        catch (Exception ex) { Log.Error("restore on exit failed", ex); }
        try { Settings.Dispose(); } catch (Exception ex) { Log.Error("settings shutdown failed", ex); }
        try { _packWatcher?.Dispose(); } catch { }
        try { Dock.Dispose(); } catch { }
        try { TopBar?.Dispose(); } catch { }
        try { Palette?.Dispose(); } catch { }
        try { LaunchpadWin?.Dispose(); } catch { }
        try { SettingsWin?.Close(); } catch { }
        try { Tray.Dispose(); } catch { }
        try { Tiling.Dispose(); } catch { }
        try { Running.Dispose(); } catch { }
        try { Msg.Dispose(); } catch { }
        try { Worker.Dispose(); } catch { }
        try { _trayWorker?.Dispose(); } catch { }
        try { _single.Dispose(); } catch { }
        Exit();
    }

    // ------------------------------------------------------------------ crash safety

    internal static void TryLog(string message, Exception? ex) { try { _emergencyLog?.Error(message, ex); } catch { } }

    /// <summary>Called for fatal errors: never leave the user without a taskbar.</summary>
    internal static void EmergencyRestore(Exception? ex)
    {
        TryLog("FATAL", ex);
        try { _current?.Tiling?.EmergencyRestore(); } catch { }
        try { _emergencyTaskbar?.Restore(); } catch { /* the record on disk lets the next start recover */ }
    }
}
