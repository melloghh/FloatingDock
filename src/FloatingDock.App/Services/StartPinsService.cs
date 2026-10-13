using FloatingDock.Windows.Shell;
using FloatingDock.Core.Configuration;

namespace FloatingDock.UI.Services;

internal sealed class StartPinsService
{
    private readonly ShellWorker _worker;
    private Task<StartPinnedCatalog.Result>? _loading;
    private StartPinnedCatalog.Result? _cached;
    private DateTime _loaded;
    private readonly string? _cacheFile;
    internal StartPinnedCatalog.Result? Cached=>_cached;
    internal StartPinsService(ShellWorker worker,string? cacheFile=null)
    {
        _worker=worker;_cacheFile=cacheFile;
        try
        {
            if(cacheFile is not null&&File.Exists(cacheFile)&&new FileInfo(cacheFile).Length<1024*1024)
            {
                var saved=JsonFile.TryLoad<StartPinnedCatalog.Result>(cacheFile);
                if(saved?.Apps is {Count:<=256}&&saved.Apps.All(a=>a is not null&&!string.IsNullOrWhiteSpace(a.Name)))_cached=saved;
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { /* Optional cache. */ }
    }
    internal async Task<StartPinnedCatalog.Result> GetAsync(bool force = false)
    {
        if (_loading is not null) return await _loading;
        if (!force && _cached is not null && DateTime.UtcNow - _loaded < TimeSpan.FromMinutes(2)) return _cached;
        _loading = StartPinnedCatalog.LoadAsync(_worker);
        try
        {
            _cached = await _loading; _loaded = DateTime.UtcNow;
            if(_cacheFile is not null)
                try { await Task.Run(()=>JsonFile.Save(_cacheFile,_cached)); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { /* Optional cache never blocks the library. */ }
            return _cached;
        }
        finally { _loading = null; }
    }
}
