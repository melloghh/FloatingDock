using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.Ipc;
using FloatingDock.Windows.Shell;

namespace FloatingDock.Engine;

/// <summary>
/// Search: apps (pinned, Launchpad tiles, installed) and two commands. Port of the WinUI command palette's data side; the
/// window itself is the UI's. The installed-apps catalogue is read once per five minutes.
/// </summary>
internal sealed partial class EngineHost
{
    private SearchUsage? _usage;
    private Task<IReadOnlyList<AppRef>>? _catalog;
    private DateTime _catalogTime;
    private AppSearchIndex? _appIndex;
    private IReadOnlyList<AppRef> _searchApps = Array.Empty<AppRef>();
    private readonly object _searchGate = new();

    private void RegisterSearchHandlers(IpcServer server)
    {
        _pinned.Changed += InvalidateSearchApps;
        _tiles.Changed += InvalidateSearchApps;
        server.Register("search.query", async request =>
        {
            string query = (request.Params["text"]?.GetValue<string>() ?? "").Trim();
            var settings = _settings.Current;
            _usage ??= await _worker.Run(() => SearchUsage.Load(_paths.SearchUsageFile));
            if (query.Length == 0 && !_usage.HasEntries) return SearchResult([], "idle");

            Task<IReadOnlyList<AppRef>> catalog;
            lock (_searchGate)
            {
                if (_catalog is not null && DateTime.UtcNow - _catalogTime > TimeSpan.FromMinutes(5)) { _catalog = null; _appIndex = null; }
                if (_catalog is null)
                {
                    _catalogTime = DateTime.UtcNow;
                    _catalog = _worker.Run<IReadOnlyList<AppRef>>(() => AppCatalog.Load());
                }
                catalog = _catalog;
            }
            var installed = await catalog;
            AppSearchIndex index;
            IReadOnlyList<AppRef> apps;
            lock (_searchGate)
            {
                if (_appIndex is null)
                {
                    _searchApps = _tiles.Snapshot().Concat(_pinned.Snapshot()).Concat(installed).Where(PaletteAppPolicy.Include)
                        .GroupBy(a => a.Key).Select(g => g.First()).ToArray();
                    _appIndex = new AppSearchIndex(_searchApps);
                }
                index = _appIndex;
                apps = _searchApps;
            }

            var results = new List<JsonObject>();
            if (query.Length > 0)
            {
                string normalized = AppSearchIndex.Normalize(query);
                foreach (var (id, name) in new[] { ("cmd:settings", "FloatingDock settings"), ("cmd:launchpad", "Launchpad") })
                {
                    var shown = InterfaceText.Get(name, settings.Language);
                    if (AppSearchIndex.Normalize(shown).Contains(normalized) || AppSearchIndex.Normalize(name).Contains(normalized))
                        results.Add(new JsonObject { ["id"] = id, ["kind"] = "Command", ["name"] = name });
                }
            }
            var found = query.Length == 0 ? _usage.Suggestions(apps, Math.Min(8, settings.SearchResultLimit)) : index.Search(query);
            foreach (var app in found.Take(settings.SearchResultLimit))
                results.Add(new JsonObject { ["id"] = "app:" + app.Key, ["kind"] = "App", ["name"] = app.Name, ["app"] = AppNode(app) });
            return SearchResult(results.Take(settings.SearchResultLimit), results.Count == 0 ? "none" : "ok");
        });
        server.Register("search.open", async request =>
        {
            var id = request.Params["id"]?.GetValue<string>() ?? throw BadParams("id is required.");
            if (id == "cmd:settings") { UiCommand("settings"); return null; }
            if (id == "cmd:launchpad") { UiCommand("toggleLaunchpad"); return null; }
            if (!id.StartsWith("app:", StringComparison.Ordinal)) throw BadParams("Unknown result.");
            string key = id[4..];
            AppRef? app;
            lock (_searchGate) app = _searchApps.FirstOrDefault(a => a.Key == key);
            if (app is null) throw BadParams("That result is no longer available.");
            _usage ??= await _worker.Run(() => SearchUsage.Load(_paths.SearchUsageFile));
            var usage = _usage;
            _ = _worker.Run(() =>
            {
                if (!ShellLauncher.Launch(app)) return;
                usage.Record(app, DateTime.UtcNow);
                try { JsonFile.Save(_paths.SearchUsageFile, usage.Snapshot()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { EngineLog.Warn("Search suggestions could not be saved: " + ex.Message); }
            });
            return null;
        });
    }

    private static JsonObject SearchResult(IEnumerable<JsonObject> results, string status)
    {
        var list = new JsonArray();
        foreach (var r in results) list.Add(r);
        return new JsonObject { ["results"] = list, ["status"] = status };
    }

    private void InvalidateSearchApps() { lock (_searchGate) _appIndex = null; }
}
