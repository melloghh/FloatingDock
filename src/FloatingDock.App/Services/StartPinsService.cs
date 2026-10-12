using FloatingDock.Windows.Shell;

namespace FloatingDock.UI.Services;

internal sealed class StartPinsService
{
    private readonly ShellWorker _worker;
    private Task<StartPinnedCatalog.Result>? _loading;
    private StartPinnedCatalog.Result? _cached;
    private DateTime _loaded;
    internal StartPinsService(ShellWorker worker) => _worker = worker;
    internal async Task<StartPinnedCatalog.Result> GetAsync(bool force = false)
    {
        if (_loading is not null) return await _loading;
        if (!force && _cached is not null && DateTime.UtcNow - _loaded < TimeSpan.FromMinutes(2)) return _cached;
        _loading = StartPinnedCatalog.LoadAsync(_worker);
        try { _cached = await _loading; _loaded = DateTime.UtcNow; return _cached; }
        finally { _loading = null; }
    }
}
