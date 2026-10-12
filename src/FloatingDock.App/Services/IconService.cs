using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Shell;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace FloatingDock.UI.Services;

/// <summary>
/// Resolves the picture of an app: user override > active icon pack > icon from the Windows shell.
/// Shared by the Dock and the Launchpad, so both always show the same icon. UI-thread only.
/// </summary>
internal sealed class IconService
{
    private readonly IconResolver _resolver;
    private readonly ShellWorker _worker;
    private readonly ShellIconExtractor _extractor;
    private readonly Dictionary<string, Task<BitmapSource?>> _cache = new();
    private readonly Dictionary<string, Task<byte[]?>> _bytesCache = new();
    private readonly Dictionary<string, Task<IconRaster?>> _rasterCache = new();
    internal int RasterDecodes { get; private set; }
    private const int MaxCacheEntries = 256;
    private int _generation;

    public IconService(IconResolver resolver, ShellWorker worker, ShellIconExtractor extractor)
    {
        _resolver = resolver;
        _worker = worker;
        _extractor = extractor;
    }

    /// <summary>Raised after overrides / packs changed; consumers re-bind their images.</summary>
    public event Action? IconsChanged;

    public void Invalidate()
    {
        _generation++;
        _cache.Clear();
        _bytesCache.Clear();
        _rasterCache.Clear();
        IconsChanged?.Invoke();
    }

    /// <param name="systemFallback">false for the built-in Start / Launchpad buttons (they have drawn glyphs instead).</param>
    public Task<BitmapSource?> GetAsync(AppRef app, int px, bool systemFallback = true)
    {
        px = Math.Clamp(px, 16, 256);
        var key = $"{_generation}|{app.Key}|{px}|{systemFallback}";
        if (!_cache.TryGetValue(key, out var task))
        {
            if (_cache.Count >= MaxCacheEntries) _cache.Remove(_cache.Keys.First());
            task = LoadAsync(app, px, systemFallback);
            _cache[key] = task;
        }
        return task;
    }

    private async Task<BitmapSource?> LoadAsync(AppRef app, int px, bool systemFallback)
    {
        try
        {
            var raster = await GetRasterAsync(app, px, systemFallback);
            if (raster is null) return null;
            // Copy the cached BGRA once. Re-encoding PNG and decoding it again wasted CPU and delayed cold Launchpad preparation.
            var bitmap = new WriteableBitmap(raster.Width, raster.Height);
            using (var buffer = bitmap.PixelBuffer.AsStream()) buffer.Write(raster.Pixels);
            bitmap.Invalidate();
            return bitmap;
        }
        catch (Exception ex)
        {
            DockApp.Current.Log.Warn($"icon load failed for {app.Name}: {ex.Message}");
            return null;
        }
    }

    internal Task<IconRaster?> GetRasterAsync(AppRef app, int px, bool systemFallback = true)
    {
        px = Math.Clamp(px, 16, 256);
        var key = $"{_generation}|{app.Key}|{px}|{systemFallback}";
        if (!_rasterCache.TryGetValue(key, out var task))
        {
            if (_rasterCache.Count >= MaxCacheEntries) _rasterCache.Remove(_rasterCache.Keys.First());
            task = LoadRasterAsync(app, px, systemFallback);
            _rasterCache[key] = task;
        }
        return task;
    }

    private async Task<IconRaster?> LoadRasterAsync(AppRef app, int px, bool systemFallback)
    {
        try
        {
            var bytes = await GetBytesAsync(app, px, systemFallback);
            if (bytes is null) return null;
            RasterDecodes++;
            try { return await IconBitmapDecoder.DecodeAsync(bytes, px); }
            catch (Exception ex) when (systemFallback)
            {
                DockApp.Current.Log.Warn("custom raster decode failed; using system icon: " + ex.Message);
                bytes = await GetSystemBytesAsync(app, px);
                return bytes is null ? null : await IconBitmapDecoder.DecodeAsync(bytes, px);
            }
        }
        catch (Exception ex) { DockApp.Current.Log.Warn("icon raster load failed: " + ex.Message); return null; }
    }

    // Composition sprites read the validated original image directly: no hidden XAML Image or visual-surface capture.
    internal Task<byte[]?> GetBytesAsync(AppRef app, int px, bool systemFallback)
    {
        px = Math.Clamp(px, 16, 256);
        var key = $"{_generation}|{app.Key}|{px}|{systemFallback}";
        if (!_bytesCache.TryGetValue(key, out var task))
        {
            if (_bytesCache.Count >= MaxCacheEntries) _bytesCache.Remove(_bytesCache.Keys.First());
            task = LoadBytesAsync(app, px, systemFallback);
            _bytesCache[key] = task;
        }
        return task;
    }

    private async Task<byte[]?> LoadBytesAsync(AppRef app, int px, bool systemFallback)
    {
        try
        {
            var bytes = await Task.Run(() => ReadLimited(_resolver.Resolve(app)?.Path));
            if (bytes is not null || !systemFallback) return bytes;
            return await GetSystemBytesAsync(app, px);
        }
        catch (Exception ex) { DockApp.Current.Log.Warn("composition icon load failed: " + ex.Message); return null; }
    }

    internal async Task<byte[]?> GetSystemBytesAsync(AppRef app, int px)
    {
        var file = await _worker.Run(() => _extractor.GetIconPng(app, Math.Clamp(px, 16, 256)));
        return await Task.Run(() => ReadLimited(file));
    }

    internal static byte[]? ReadLimited(string? file)
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
                DockApp.Current.Log.Warn($"icon rejected ({Path.GetFileName(file)}): {why}");
                return null;
            }
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<BitmapImage> DecodeAsync(byte[] bytes, int decodePx)
    {
        using var ms = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ms))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        ms.Seek(0);
        var bmp = new BitmapImage { DecodePixelWidth = decodePx };
        await bmp.SetSourceAsync(ms);
        return bmp;
    }
}
