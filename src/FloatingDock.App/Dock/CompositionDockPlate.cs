using System.Numerics;
using FloatingDock.Core.Configuration;
using Windows.UI.Composition;

namespace FloatingDock.UI.Dock;

/// <summary>Cached nine-grid plate and a compositor-blurred shadow. Hover only changes GPU bounds.</summary>
internal sealed class CompositionDockPlate : IDisposable
{
    internal ContainerVisual Visual { get; }
    private readonly SystemDockGraphics _graphics;
    private readonly SpriteVisual _plate, _shadowVisual;
    private readonly DropShadow _shadow;
    private readonly List<IDisposable> _resources = new();
    private string? _style;
    private (float, float, float, float) _lastBounds;
    internal int Rasterizations { get; private set; }

    internal CompositionDockPlate(SystemDockGraphics graphics)
    {
        _graphics = graphics;
        var c = graphics.Compositor;
        Visual = c.CreateContainerVisual();
        _shadowVisual = c.CreateSpriteVisual(); _plate = c.CreateSpriteVisual();
        _shadow = c.CreateDropShadow();
        _shadow.Color = global::Windows.UI.Color.FromArgb(255, 0, 0, 0);
        _shadow.BlurRadius = 16; _shadow.Offset = new Vector3(0, 7, 0); _shadow.Opacity = .30f;
        _shadowVisual.Shadow = _shadow;
        Visual.Children.InsertAtTop(_shadowVisual); Visual.Children.InsertAtTop(_plate);
    }

    internal void SetViewport(float width, float height) => Visual.Size = new Vector2(width, height);

    internal void SetStyle(DockSettings s)
    {
        string key = $"{s.BackgroundColor}|{s.Opacity}|{s.CornerRadius}|{s.DockHeight}|{s.Border}|{s.Shadow}";
        if (_style == key) return;
        _style = key;
        _plate.Brush = null; _shadow.Mask = null;
        foreach (var r in _resources) r.Dispose(); _resources.Clear();
        const int factor = 2;
        float radius = Math.Min(s.CornerRadius, s.DockHeight / 2f);
        int width = (int)Math.Ceiling(radius * 2 + 2) * factor, height = s.DockHeight * factor;
        var pixels = new byte[width * height * 4]; var mask = new byte[pixels.Length];
        ColorUtil.TryParseRgb(s.BackgroundColor, out var rgb);
        float rpx = radius * factor, halfW = width * .5f, halfH = height * .5f;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            float dx = Math.Abs(x + .5f - halfW) - (halfW - rpx);
            float dy = Math.Abs(y + .5f - halfH) - (halfH - rpx);
            float d = MathF.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0)) + Math.Min(Math.Max(dx, dy), 0) - rpx;
            float coverage = Math.Clamp(.5f - d, 0, 1);
            float border = s.Border ? (coverage - Math.Clamp(.5f - d - factor, 0, 1)) * (70f / 255) : 0;
            float fill = coverage * s.Opacity / 100f;
            int i = (y * width + x) * 4;
            pixels[i] = (byte)Math.Clamp(rgb.B * fill * (1 - border) + 255 * border, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(rgb.G * fill * (1 - border) + 255 * border, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(rgb.R * fill * (1 - border) + 255 * border, 0, 255);
            pixels[i + 3] = (byte)Math.Clamp((border + fill * (1 - border)) * 255, 0, 255);
            mask[i] = mask[i + 1] = mask[i + 2] = mask[i + 3] = (byte)(coverage * 255);
        }
        _plate.Brush = Brush(pixels, width, height, radius * factor);
        if (s.Shadow) _shadow.Mask = Brush(mask, width, height, radius * factor);
        _shadowVisual.Opacity = s.Shadow ? 1 : 0;
        Rasterizations++;
    }

    private CompositionNineGridBrush Brush(byte[] pixels, int width, int height, float inset)
    {
        var surface = _graphics.Upload(pixels, width, height); _resources.Add(surface);
        var source = _graphics.Compositor.CreateSurfaceBrush(surface); _resources.Add(source);
        source.Stretch = CompositionStretch.Fill;
        var brush = _graphics.Compositor.CreateNineGridBrush(); _resources.Add(brush); brush.Source = source;
        brush.LeftInset = brush.RightInset = brush.TopInset = brush.BottomInset = inset;
        brush.LeftInsetScale = brush.RightInsetScale = brush.TopInsetScale = brush.BottomInsetScale = .5f;
        return brush;
    }

    internal void SetBounds(float left, float top, float width, float height)
    {
        var bounds = (left, top, width, height); if (_lastBounds == bounds) return; _lastBounds = bounds;
        _plate.Offset = _shadowVisual.Offset = new Vector3(left, top, 0);
        _plate.Size = _shadowVisual.Size = new Vector2(width, height);
    }

    public void Dispose()
    {
        _plate.Brush = null; _shadow.Mask = null;
        Visual.Children.RemoveAll();
        foreach (var r in _resources) r.Dispose();
        _shadow.Dispose(); _shadowVisual.Dispose(); _plate.Dispose(); Visual.Dispose();
    }
}
