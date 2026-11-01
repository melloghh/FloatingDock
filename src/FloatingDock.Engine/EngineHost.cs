using System.Text.Json;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.Ipc;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.Shell;

namespace FloatingDock.Engine;

/// <summary>Owns the long-lived services and exposes them over IPC. Everything the UI may ask for is registered in <see cref="RegisterHandlers"/>.</summary>
internal sealed class EngineHost : IDisposable
{
    private readonly AppPaths _paths;
    private readonly UiLoop _ui;
    private readonly MediaController _media = new();
    private SettingsStore? _settings;
    private AppListStore? _pinned;
    private ShellWorker? _worker;
    private RunningAppsService? _running;
    private IpcServer? _server;

    public EngineHost(AppPaths paths, UiLoop ui)
    {
        _paths = paths;
        _ui = ui;
    }

    public static string Version => typeof(EngineHost).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>Must run on the UI-loop thread: the WinEvent hooks belong to it.</summary>
    public void Start()
    {
        _paths.EnsureCreated();
        _settings = new SettingsStore(_paths.ConfigFile);
        _settings.SaveFailed += ex => EngineLog.Warn("settings could not be saved: " + ex.Message);
        _pinned = new AppListStore(_paths.PinnedFile);
        _worker = new ShellWorker();
        _running = new RunningAppsService(_worker, _ui.Post);

        _server = new IpcServer(IpcEndpoint.SocketPathFor(_paths.Root), Version);
        _server.SessionOpened += s => EngineLog.Info($"client #{s.Id} connected");
        _server.SessionClosed += s => EngineLog.Info($"client #{s.Id} ({s.ClientName}) disconnected");
        RegisterHandlers(_server);
        _server.Start();

        _settings.Changed += s => _server.Publish(Protocol.Events.SettingsChanged, SettingsNode(s));
        _pinned.Changed += () => _server.Publish(Protocol.Events.PinnedChanged, PinnedNode());
        _running.Changed += (_, _) => _server.Publish(Protocol.Events.WindowsChanged, WindowsNode());
        _running.Start();
        EngineLog.Info($"engine {Version} serving {IpcEndpoint.SocketPathFor(_paths.Root)} (profile {_paths.Root}, settings {_settings.LoadStatus})");
    }

    private void RegisterHandlers(IpcServer server)
    {
        server.Register(Protocol.Methods.SettingsGet, _ => Task.FromResult<JsonNode?>(SettingsNode(_settings!.Current)));
        server.Register(Protocol.Methods.SettingsSet, request =>
        {
            var patch = request.Params["patch"] as JsonObject
                ?? throw new IpcException(Protocol.Errors.BadParams, "patch must be an object of setting names and values.");
            DockSettings next;
            try { next = SettingsPatch.Apply(_settings!.Current, patch); }
            catch (SettingsPatchException ex) { throw new IpcException(Protocol.Errors.BadParams, ex.Key.Length == 0 ? ex.Message : ex.Key + ": " + ex.Message); }
            _settings.Update(target => SettingsPatch.CopyInto(next, target));
            return Task.FromResult<JsonNode?>(SettingsNode(_settings.Current));
        });
        server.Register(Protocol.Methods.PinnedGet, _ => Task.FromResult<JsonNode?>(PinnedNode()));
        server.Register(Protocol.Methods.WindowsList, async _ => await _ui.InvokeAsync(WindowsNode));
        server.Register(Protocol.Methods.MediaGet, async _ => MediaNode(await _media.ReadAsync()));
        server.Register(Protocol.Methods.MediaControl, async request =>
        {
            var command = request.Params["command"]?.GetValue<string>()
                ?? throw new IpcException(Protocol.Errors.BadParams, "command is required.");
            return new JsonObject { ["ok"] = await _media.ControlAsync(command) };
        });
        server.Register(Protocol.Methods.EngineQuit, request =>
        {
            EngineLog.Info($"quit requested by {request.Session.ClientName}");
            _ = Task.Run(async () => { await Task.Delay(150); _ui.Quit(); });      // let the reply go out first
            return Task.FromResult<JsonNode?>(new JsonObject { ["quitting"] = true });
        });
    }

    private static JsonNode SettingsNode(DockSettings s) => JsonSerializer.SerializeToNode(s, JsonFile.Options)!;

    private JsonNode PinnedNode() => JsonSerializer.SerializeToNode(_pinned!.Snapshot(), JsonFile.Options)!;

    /// <summary>Must run on the UI-loop thread (the running-apps service is not thread-safe).</summary>
    private JsonObject WindowsNode()
    {
        var windows = new JsonArray();
        foreach (var w in _running!.Snapshot())
            windows.Add(new JsonObject
            {
                ["hwnd"] = w.Hwnd,
                ["pid"] = w.ProcessId,
                ["exePath"] = w.ExePath,
                ["aumid"] = w.Aumid,
                ["title"] = w.Title,
                ["className"] = w.ClassName,
                ["minimized"] = w.IsMinimized,
            });
        return new JsonObject { ["foreground"] = _running.ForegroundRoot, ["windows"] = windows };
    }

    private static JsonNode? MediaNode(MediaState? m) => m is null ? null : new JsonObject
    {
        ["title"] = m.Title,
        ["artist"] = m.Artist,
        ["source"] = m.Source,
        ["canPrevious"] = m.Previous,
        ["canToggle"] = m.Toggle,
        ["canNext"] = m.Next,
        ["playing"] = m.Playing,
        ["position"] = m.Position,
        ["duration"] = m.Duration,
        ["canShuffle"] = m.CanShuffle,
        ["shuffle"] = m.Shuffle,
        ["canRepeat"] = m.CanRepeat,
        ["repeat"] = m.Repeat,
    };

    public void Dispose()
    {
        _running?.Dispose();
        _server?.Dispose();
        _worker?.Dispose();
        _settings?.Dispose();
        EngineLog.Info("engine stopped");
    }
}
