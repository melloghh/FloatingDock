using System.Numerics;

using Windows.UI.Composition;






namespace FloatingDock.UI.Dock;

/// <summary>Each tooltip is rasterized once on a worker. Only a cached sprite moves during hover.</summary>
internal sealed class CompositionLabel : IDisposable
{
    private sealed record Texture(CompositionDrawingSurface Surface, Vector2 Size);

    private readonly SystemDockGraphics _graphics;

    private readonly Action _invalidate;
    private readonly CompositionSurfaceBrush _brush;
    private readonly Dictionary<string, Texture> _cache = new();
    private readonly HashSet<string> _pending = new();
    private readonly Queue<(string Key, string Text, double Scale)> _requests = new();
    private string? _wanted;
    private string? _selectedText;
    private double _selectedScale;
    private bool _working, _disposed;
    internal SpriteVisual Visual { get; }
    internal Vector2 Size { get; private set; }
    private Vector3 _offset = new(float.NaN);
    private float _opacity;
    internal void SetOffset(Vector3 offset)
    {
        if (_offset == offset) return;
        Visual.Offset = offset; _offset = offset;
    }
    internal void SetOpacity(float opacity)
    {
        if (_opacity == opacity) return;
        Visual.Opacity = opacity; _opacity = opacity;
    }
    internal int Rasterizations { get; private set; }
    internal bool IsPreparing => _working;

    internal CompositionLabel(SystemDockGraphics graphics, Action invalidate)
    {
        _graphics = graphics; var compositor = graphics.Compositor; _invalidate = invalidate;
        _brush = compositor.CreateSurfaceBrush(); _brush.Stretch = CompositionStretch.Fill;
        Visual = compositor.CreateSpriteVisual(); Visual.Brush = _brush; Visual.Opacity = 0;
    }

    internal bool Select(string text, double scale)
    {
        if (_selectedText == text && _selectedScale == scale && _wanted is not null && _cache.ContainsKey(_wanted)) return true;
        string key = text + "|" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _wanted = key; _selectedText = text; _selectedScale = scale;
        if (_cache.TryGetValue(key, out var texture)) { _brush.Surface = texture.Surface; Visual.Size = Size = texture.Size; return true; }
        Prepare(text, scale); return false;
    }

    internal void Prepare(string text, double scale)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        string key = text + "|" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_cache.ContainsKey(key) || !_pending.Add(key)) return;
        // Bounded queue/cache: a large app library never creates unbounded tooltip resources.
        if (_requests.Count >= 64) { _pending.Remove(key); return; }
        _requests.Enqueue((key, text, scale));
        if (!_working) _ = ProcessAsync();
    }

    private async Task ProcessAsync()
    {
        _working = true;
        try
        {
            while (!_disposed && _requests.TryDequeue(out var request))
            {
                CompositionDrawingSurface? surface = null;
                try
                {
                    var image = await Task.Run(() => LabelRasterizer.Render(request.Text, request.Scale));
                    if (_disposed) break;
                    surface = _graphics.Upload(image.Pixels, image.Width, image.Height);
                    var texture = new Texture(surface, new Vector2((float)(image.Width / request.Scale), (float)(image.Height / request.Scale)));
                    if (_cache.Count >= 64)
                    {
                        var oldest = _cache.FirstOrDefault(p => p.Key != _wanted);
                        if (oldest.Key is not null) { _cache.Remove(oldest.Key); oldest.Value.Surface.Dispose(); }
                    }
                    _cache[request.Key] = texture; surface = null; Rasterizations++;
                    if (_wanted == request.Key) { _brush.Surface = texture.Surface; Visual.Size = Size = texture.Size; }
                    _invalidate();
                }
                catch (Exception ex) { if (!_disposed) DockApp.Current.Log.Warn("label preparation failed: " + ex.Message); }
                finally { surface?.Dispose(); _pending.Remove(request.Key); }
            }
        }
        finally { _working = false; }
    }

    public void Dispose()
    {
        _disposed = true; _brush.Surface = null; Visual.Dispose(); _brush.Dispose();
        foreach (var texture in _cache.Values) texture.Surface.Dispose(); _cache.Clear(); _requests.Clear();
    }
}



