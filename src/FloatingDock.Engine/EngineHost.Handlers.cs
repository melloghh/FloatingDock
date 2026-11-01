using System.Text.Json;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.Ipc;
using FloatingDock.Windows.Shell;

namespace FloatingDock.Engine;

internal sealed partial class EngineHost
{
    private static IpcException BadParams(string message) => new(Protocol.Errors.BadParams, message);

    private static string RequireString(IpcRequest r, string name) =>
        r.Params[name]?.GetValue<string>() ?? throw BadParams(name + " is required.");

    private static AppRef RequireApp(IpcRequest r)
    {
        try { return r.Params["app"].Deserialize<AppRef>(JsonFile.Options) ?? throw BadParams("app is required."); }
        catch (JsonException ex) { throw BadParams("app is not an app description: " + ex.Message); }
    }

    private void RegisterHandlers(IpcServer server)
    {
        // ---- state
        server.RegisterSync(Protocol.Methods.SettingsGet, _ => SettingsNode(_settings.Current));
        server.RegisterSync(Protocol.Methods.SettingsSet, request =>
        {
            var patch = request.Params["patch"] as JsonObject ?? throw BadParams("patch must be an object of setting names and values.");
            DockSettings next;
            try { next = SettingsPatch.Apply(_settings.Current, patch); }
            catch (SettingsPatchException ex) { throw BadParams(ex.Key.Length == 0 ? ex.Message : ex.Key + ": " + ex.Message); }
            _settings.Update(target => SettingsPatch.CopyInto(next, target));
            return SettingsNode(_settings.Current);
        });
        server.RegisterSync("settings.reset", _ =>
        {
            var defaults = new DockSettings();
            _settings.Update(target => SettingsPatch.CopyInto(defaults, target));
            return SettingsNode(_settings.Current);
        });
        server.RegisterSync("text.table", _ =>
        {
            var language = _settings.Current.Language;
            var table = new JsonObject();
            foreach (var (english, translated) in InterfaceText.Table(language)) table[english] = translated;
            return new JsonObject { ["language"] = language.ToString(), ["table"] = table };
        });
        server.Register(Protocol.Methods.WindowsList, async _ => await _ui.InvokeAsync(WindowsNode));
        server.Register(Protocol.Methods.MediaGet, async _ => MediaNode(await _media.ReadAsync()));
        server.Register(Protocol.Methods.MediaControl, async request =>
            new JsonObject { ["ok"] = await _media.ControlAsync(RequireString(request, "command")) });

        // ---- app
        server.RegisterSync("app.info", _ => new JsonObject
        {
            ["version"] = Version, ["dataPath"] = _paths.Root, ["logsPath"] = _paths.LogsDir, ["packsPath"] = _packs.RootDirectory,
            ["pid"] = Environment.ProcessId, ["diagnostic"] = _diagnostic,
        });
        server.RegisterSync(Protocol.Methods.EngineQuit, request => Quit(request.Session.ClientName ?? "?"));
        server.RegisterSync("app.quit", request => Quit(request.Session.ClientName ?? "?"));
        server.RegisterSync("app.restart", _ =>
        {
            _ui.Post(Restart);
            return new JsonObject { ["restarting"] = true };
        });
        server.RegisterSync("shell.open", request =>
        {
            var folder = RequireString(request, "target") switch
            {
                "data" => _paths.Root, "logs" => _paths.LogsDir, "packs" => _packs.RootDirectory,
                var other => throw BadParams("Unknown folder: " + other),
            };
            ShellLauncher.OpenFolder(folder);
            return null;
        });
        server.RegisterSync("taskbar.restore", _ =>
        {
            _ui.Post(RestoreTaskbarNow);
            return null;
        });
        server.RegisterSync("hotkey.status", _ => HotkeyStatusNode());
        server.RegisterSync("dock.setHidden", request =>
        {
            _ui.Post(() => SetDockUserHidden(request.Params["hidden"]?.GetValue<bool>() ?? false));
            return null;
        });

        // ---- pinned apps and Launchpad tiles
        server.RegisterSync(Protocol.Methods.PinnedGet, _ => PinnedNode());
        server.RegisterSync("pinned.move", request =>
        {
            _pinned.MoveApplication(RequireString(request, "key"), request.Params["index"]?.GetValue<int>() ?? 0);
            return null;
        });
        server.RegisterSync("pinned.remove", request => new JsonObject { ["removed"] = _pinned.Remove(RequireString(request, "key")) });
        server.Register("pinned.add", async request =>
        {
            var path = RequireString(request, "path");
            var r = await _worker.Run(() => UserTileImporter.ImportAndSave(path, _paths.AppLibraryShortcutsDir, _pinned));
            return new JsonObject { ["status"] = r.Status.ToString(), ["message"] = r.Message };
        });
        server.Register("pinned.importTaskbar", async _ =>
        {
            var pins = await _worker.Run(AppCatalog.ReadWindowsTaskbarPins);
            int added = 0;
            foreach (var p in pins) if (_pinned.Add(p)) added++;
            return new JsonObject { ["added"] = added };
        });
        server.RegisterSync("tiles.get", _ => AppsNode(_tiles.Snapshot()));
        server.RegisterSync("tiles.remove", request => new JsonObject { ["removed"] = _tiles.Remove(RequireString(request, "key")) });

        // ---- icons and icon packs
        server.Register("icons.get", async request =>
        {
            var app = RequireApp(request);
            int size = request.Params["size"]?.GetValue<int>() ?? 48;
            bool fallback = request.Params["fallback"]?.GetValue<bool>() ?? true;
            var bytes = await _icons.GetPngAsync(app, size, fallback);
            return new JsonObject { ["image"] = bytes is null ? null : Convert.ToBase64String(bytes) };
        });
        server.RegisterSync("appicons.list", _ =>
        {
            var list = new JsonArray();
            foreach (var app in _pinned.Snapshot().Concat(_tiles.Snapshot()).Where(a => !a.IsSeparator).GroupBy(a => a.Key).Select(g => g.First()))
            {
                var node = AppNode(app);
                node["hasOverride"] = _overrides.Has(app);
                list.Add(node);
            }
            return list;
        });
        server.Register("icons.override", async request =>
        {
            var key = RequireString(request, "key");
            var path = RequireString(request, "path");
            var bytes = await Task.Run(() => IconProvider.ReadLimited(path))
                ?? throw BadParams("The file is not a valid PNG or ICO within the icon limits.");
            _overrides.Set(key, path, bytes);
            return null;
        });
        server.RegisterSync("icons.reset", request => new JsonObject { ["reset"] = _overrides.Reset(RequireString(request, "key")) });
        server.RegisterSync("packs.list", _ =>
        {
            var list = new JsonArray();
            foreach (var p in _packs.ListPacks())
                list.Add(new JsonObject
                {
                    ["id"] = p.Id, ["folder"] = Path.GetFileName(p.Directory), ["name"] = p.Name, ["version"] = p.Version,
                    ["description"] = p.Description, ["iconCount"] = p.IconCount, ["isBuiltIn"] = p.IsBuiltIn,
                    ["warnings"] = new JsonArray(p.Warnings.Select(w => (JsonNode?)w).ToArray()),
                });
            return list;
        });
        server.RegisterSync("packs.apply", request =>
        {
            var pack = _packs.Load(RequireString(request, "folder")) ?? throw BadParams("Unknown icon pack.");
            var folder = Path.GetFileName(pack.Directory);
            _settings.Update(d => { d.ActiveIconPack = folder; d.UseIconPack = true; });
            foreach (var app in _pinned.Snapshot().Concat(_tiles.Snapshot()).Where(a => !a.IsSeparator))
                if (pack.Find(app.IconNames(), _settings.Current.PreferDarkVariants) is not null) _overrides.ResetForApp(app);
            _ui.Post(_icons.Invalidate);
            return null;
        });
        server.Register("packs.inspect", async request =>
        {
            var path = RequireString(request, "path");
            try
            {
                var p = await Task.Run(() => _packs.Inspect(path));
                return new JsonObject
                {
                    ["name"] = p.Name, ["version"] = p.Version, ["description"] = p.Description, ["iconCount"] = p.IconCount,
                    ["iconNames"] = new JsonArray(p.IconNames.Take(20).Select(n => (JsonNode?)n).ToArray()),
                    ["problems"] = new JsonArray(p.Problems.Select(n => (JsonNode?)n).ToArray()),
                };
            }
            catch (Exception ex) when (ex is IconPackImportException or IOException or InvalidDataException) { throw BadParams(ex.Message); }
        });
        server.Register("packs.import", async request =>
        {
            var path = RequireString(request, "path");
            try
            {
                var pack = await Task.Run(() => _packs.Import(path, overwrite: true));
                var folder = Path.GetFileName(pack.Directory);
                _settings.Update(d => { d.ActiveIconPack = folder; d.UseIconPack = true; });
                _resolver.Reload();
                _ui.Post(() => { _icons.Invalidate(); _server.Publish("packs.changed", null); });
                return new JsonObject { ["folder"] = folder };
            }
            catch (Exception ex) when (ex is IconPackImportException or IOException or InvalidDataException) { throw BadParams(ex.Message); }
        });
        server.Register("packs.export", async request =>
        {
            var folder = RequireString(request, "folder");
            var path = RequireString(request, "path");
            try { await Task.Run(() => _packs.Export(folder, path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw BadParams(ex.Message); }
            return null;
        });
        server.RegisterSync("packs.delete", request =>
        {
            _packs.Delete(RequireString(request, "folder"));
            _settings.Update(d => d.ActiveIconPack = "Default");
            _resolver.Reload();
            _ui.Post(() => { _icons.Invalidate(); _server.Publish("packs.changed", null); });
            return null;
        });
        server.RegisterSync("packs.reload", _ =>
        {
            _resolver.Reload();
            _ui.Post(() => { _icons.Invalidate(); _server.Publish("packs.changed", null); });
            return null;
        });
    }

    private JsonNode Quit(string who)
    {
        EngineLog.Info($"quit requested by {who}");
        _ = Task.Run(async () => { await Task.Delay(150); _ui.Post(Shutdown); });      // let the reply go out first
        return new JsonObject { ["quitting"] = true };
    }
}
