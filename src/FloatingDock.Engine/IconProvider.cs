using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Shell;

namespace FloatingDock.Engine;

/// <summary>
/// The picture of an app as PNG bytes: user override > active icon pack > Windows shell. Shared by every UI, so the dock,
/// Launchpad and settings always show the same icon. This is the byte path of the old WinUI IconService.
/// </summary>
internal sealed class IconProvider(IconResolver resolver, ShellWorker worker, ShellIconExtractor extractor)
{
    private const int MaxCacheEntries = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<byte[]?>> _cache = new();
    private int _generation;

    /// <summary>Raised after overrides or packs changed; UIs reload their images.</summary>
    public event Action? Changed;

    public void Invalidate()
    {
        lock (_gate) { _generation++; _cache.Clear(); }
        Changed?.Invoke();
    }

    public Task<byte[]?> GetPngAsync(AppRef app, int px, bool systemFallback = true)
    {
        px = Math.Clamp(px, 16, 256);
        lock (_gate)
        {
            var key = $"{_generation}|{app.Key}|{px}|{systemFallback}";
            if (!_cache.TryGetValue(key, out var task))
            {
                if (_cache.Count >= MaxCacheEntries) _cache.Remove(_cache.Keys.First());
                task = _cache[key] = LoadAsync(app, px, systemFallback);
            }
            return task;
        }
    }

    private async Task<byte[]?> LoadAsync(AppRef app, int px, bool systemFallback)
    {
        try
        {
            var bytes = await Task.Run(() => ReadLimited(resolver.Resolve(app)?.Path));
            if (bytes is not null || !systemFallback) return bytes;
            if (AppMatching.IsExplorer(app))
            {
                var bundled = await Task.Run(() => ReadLimited(Path.Combine(AppContext.BaseDirectory, "Assets", "Explorer.png")));
                if (bundled is not null) return bundled;
            }
            var file = await worker.Run(() => extractor.GetIconPng(app, px));
            return await Task.Run(() => ReadLimited(file));
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"icon load failed for {app.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The file's bytes if it is a valid, small-enough PNG / ICO; null (and a log line) otherwise.</summary>
    public static byte[]? ReadLimited(string? file)
    {
        if (file is null || !File.Exists(file)) return null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > IconLimits.MaxFileBytes) return null;
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            using var probe = new MemoryStream(bytes, writable: false);
            if (ImageProbe.Probe(probe, out var why) is null)
            {
                EngineLog.Warn($"icon rejected ({Path.GetFileName(file)}): {why}");
                return null;
            }
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
