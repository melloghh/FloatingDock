using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Ipc;
using FloatingDock.Windows.Shell;

namespace FloatingDock.Engine;

/// <summary>The cached read of the apps pinned in Windows Start (the Launchpad's catalogue).</summary>
internal sealed class StartPinsService
{
    private readonly ShellWorker _worker;
    private Task<StartPinnedCatalog.Result>? _loading;
    private StartPinnedCatalog.Result? _cached;
    private DateTime _loaded;
    private readonly string? _cacheFile;

    public StartPinsService(ShellWorker worker, string? cacheFile = null)
    {
        _worker = worker;
        _cacheFile = cacheFile;
        try
        {
            if (cacheFile is not null && File.Exists(cacheFile) && new FileInfo(cacheFile).Length < 1024 * 1024)
            {
                var saved = JsonFile.TryLoad<StartPinnedCatalog.Result>(cacheFile);
                if (saved?.Apps is { Count: <= 256 } && saved.Apps.All(a => a is not null && !string.IsNullOrWhiteSpace(a.Name))) _cached = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* optional cache */ }
    }

    public StartPinnedCatalog.Result? Cached => _cached;

    public async Task<StartPinnedCatalog.Result> GetAsync(bool force = false)
    {
        if (_loading is not null) return await _loading;
        if (!force && _cached is not null && DateTime.UtcNow - _loaded < TimeSpan.FromMinutes(2)) return _cached;
        _loading = StartPinnedCatalog.LoadAsync(_worker);
        try
        {
            _cached = await _loading;
            _loaded = DateTime.UtcNow;
            if (_cacheFile is not null)
                try { await Task.Run(() => JsonFile.Save(_cacheFile, _cached)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* optional cache never blocks the library */ }
            return _cached;
        }
        finally { _loading = null; }
    }
}

/// <summary>Launchpad data and the app actions shared by the Launchpad, search and the dock: launch, pin, reveal, tiles.</summary>
internal sealed partial class EngineHost
{
    private StartPinsService _startPins = null!;

    private void RegisterLaunchpadHandlers(IpcServer server)
    {
        _startPins = new StartPinsService(_worker, Path.Combine(_paths.CacheDir, "start-pins.json"));
        _tiles.Changed += () => _server.Publish("launchpad.changed", null);

        server.Register("launchpad.apps", async request =>
        {
            bool refresh = request.Params["refresh"]?.GetValue<bool>() ?? false;
            var catalog = await _startPins.GetAsync(refresh);
            var user = _tiles.Snapshot();
            var userKeys = new HashSet<string>(user.Select(u => u.Key));
            var all = user.Concat(catalog.Apps.Where(a => !userKeys.Contains(a.Key))).ToList();
            return new JsonObject { ["apps"] = AppsNode(all), ["exportedPins"] = catalog.ExportedPins, ["userCount"] = user.Count };
        });

        server.RegisterSync("apps.launch", request =>
        {
            var app = RequireApp(request);
            _ = _worker.Run(() => { if (!ShellLauncher.Launch(app)) EngineLog.Warn("launch failed: " + app.Name); });
            return null;
        });
        server.RegisterSync("apps.reveal", request =>
        {
            var location = ShellLauncher.LocationOf(RequireApp(request));
            if (location is not null) ShellLauncher.RevealInExplorer(location);
            return new JsonObject { ["ok"] = location is not null };
        });
        server.RegisterSync("apps.info", request =>
        {
            var app = RequireApp(request);
            return new JsonObject
            {
                ["pinned"] = _pinned.Contains(app.Key), ["isTile"] = _tiles.Contains(app.Key),
                ["hasOverride"] = _overrides.Has(app), ["hasLocation"] = ShellLauncher.LocationOf(app) is not null,
                ["tileIndex"] = _tiles.Snapshot().Select(a => a.Key).ToList().IndexOf(app.Key), ["tileCount"] = _tiles.Snapshot().Count,
            };
        });
        server.RegisterSync("pinned.addApp", request => new JsonObject { ["added"] = _pinned.Add(RequireApp(request).Clone()) });
        server.RegisterSync("tiles.move", request =>
        {
            _tiles.Move(RequireString(request, "key"), request.Params["index"]?.GetValue<int>() ?? 0);
            return null;
        });
        server.Register("tiles.add", async request =>
        {
            var path = RequireString(request, "path");
            var r = await _worker.Run(() => UserTileImporter.ImportAndSave(path, _paths.AppLibraryShortcutsDir, _tiles));
            return new JsonObject { ["status"] = r.Status.ToString(), ["message"] = r.Message };
        });
    }
}
