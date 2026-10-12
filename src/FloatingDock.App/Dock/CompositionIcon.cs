using FloatingDock.Core.Configuration;
using System.Numerics;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using Windows.UI.Composition;

using Windows.Storage.Streams;
using Windows.UI;

namespace FloatingDock.UI.Dock;

/// <summary>Static image texture and vector fallback. No XAML element participates in per-frame icon animation.</summary>
internal sealed class CompositionIcon : IDisposable
{
    private readonly Compositor _compositor;
    private readonly SystemDockGraphics _graphics;
    private readonly ContainerVisual _content;
    private readonly ContainerVisual _pulse;
    private readonly SpriteVisual _image;
    private readonly CompositionSurfaceBrush _brush;
    private ShapeVisual? _fallback;
    private CompositionDrawingSurface? _surface;
    private AppRef? _app;
    private GlyphKind _kind;
    private int _px, _generation;
    private bool _disposed;
    internal ContainerVisual Visual { get; }

    internal CompositionIcon(SystemDockGraphics graphics)
    {
        _graphics = graphics; var compositor = graphics.Compositor; _compositor = compositor;
        Visual = compositor.CreateContainerVisual();
        _pulse=compositor.CreateContainerVisual();Visual.Children.InsertAtTop(_pulse);
        _content = compositor.CreateContainerVisual();
        _pulse.Children.InsertAtTop(_content);
        _brush = compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Uniform;
        _brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
        _image = compositor.CreateSpriteVisual();
        _image.Size = new Vector2(43.2f);
        _image.Offset = new Vector3(2.4f,2.4f,0);
        _image.Brush = _brush;
        _content.Children.InsertAtTop(_image);
    }

    internal void SetSize(float size) { Visual.Size = new Vector2(size); _pulse.CenterPoint=new Vector3(size/2,size/2,0);_content.Scale = new Vector3(size / 48, size / 48, 1); }
    internal void Pulse(FloatingDock.Core.Configuration.DockEdge edge)
    {
        if(!SurfaceMotion.Enabled||_disposed)return;
        using var ease=_compositor.CreateCubicBezierEasingFunction(new Vector2(.16f,1),new Vector2(.3f,1));
        using var scale=_compositor.CreateVector3KeyFrameAnimation();scale.Duration=TimeSpan.FromMilliseconds(420);
        scale.InsertExpressionKeyFrame(0,"this.StartingValue");scale.InsertKeyFrame(.32f,new Vector3(1.065f,1.065f,1),ease);scale.InsertKeyFrame(1,Vector3.One,ease);
        using var lift=_compositor.CreateVector3KeyFrameAnimation();lift.Duration=scale.Duration;
        var direction=edge switch { FloatingDock.Core.Configuration.DockEdge.Left=>new Vector3(5,0,0),FloatingDock.Core.Configuration.DockEdge.Right=>new Vector3(-5,0,0),_=>new Vector3(0,-5,0) };
        lift.InsertExpressionKeyFrame(0,"this.StartingValue");lift.InsertKeyFrame(.32f,direction,ease);lift.InsertKeyFrame(1,Vector3.Zero,ease);
        _pulse.StartAnimation("Scale",scale);_pulse.StartAnimation("Offset",lift);
    }
    internal void Bind(AppRef app, GlyphKind kind, int px)
    {
        if (_app?.Key == app.Key && _kind == kind && _px == px) return;
        _app = app; _kind = kind; _px = px;
        Reload();
    }

    internal async void Reload()
    {
        if (_disposed || _app is null) return;
        int generation = ++_generation;
        if (_kind == GlyphKind.Launchpad)
        {
            _brush.Surface=null;_surface?.Dispose();_surface=null;
            if(_fallback is not null){_content.Children.Remove(_fallback);_fallback.Dispose();_fallback=null;}
        }
        if (_surface is null) ShowFallback();
        CompositionDrawingSurface? candidate = null;
        try
        {
            var raster = await DockApp.Current.Icons.GetRasterAsync(_app, _px, _kind == GlyphKind.App);
            if (_disposed || generation != _generation || raster is null) return;
            candidate = _graphics.Upload(raster.Pixels, raster.Width, raster.Height);
            if (_disposed || generation != _generation) return;
            _brush.Surface = candidate;
            _surface?.Dispose(); _surface = candidate; candidate = null;
            if (_fallback is not null) { _content.Children.Remove(_fallback); _fallback.Dispose(); _fallback = null; }
        }
        catch (Exception ex) { if (!_disposed) DockApp.Current.Log.Warn("composition image decode failed: " + ex.Message); }
        finally { candidate?.Dispose(); }
    }

    private void ShowFallback()
    {
        if (_fallback is not null) return;
        var visual = _compositor.CreateShapeVisual(); visual.Size = new Vector2(48);
        void Rect(float x, float y, float width, float height, float radius, Color color)
        {
            var geometry = _compositor.CreateRoundedRectangleGeometry(); geometry.Offset = new Vector2(x, y); geometry.Size = new Vector2(width, height); geometry.CornerRadius = new Vector2(radius);
            var shape = _compositor.CreateSpriteShape(geometry); shape.FillBrush = _compositor.CreateColorBrush(color); visual.Shapes.Add(shape);
        }
        Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
        var style=DockApp.Current.Settings.Current.LaunchpadIconStyle;
        if(_kind!=GlyphKind.Launchpad||style!=LaunchpadIconStyle.WhiteSquares)Rect(0, 0, 48, 48, 11, _kind == GlyphKind.Start ? C(30, 90, 190) : _kind == GlyphKind.Launchpad ? style==LaunchpadIconStyle.ColorOnWhite?C(255,255,255):C(12,14,18) : C(68, 95, 147));
        if (_kind == GlyphKind.Start)
            for (int i = 0; i < 4; i++) Rect(12 + (i % 2) * 13, 12 + (i / 2) * 13, 11, 11, 1.5f, C(255, 255, 255));
        else if (_kind == GlyphKind.Launchpad)
        {
            Color[] colors = { C(255,95,86), C(255,189,46), C(39,201,63), C(10,132,255), C(175,82,222), C(255,255,255), C(255,140,0), C(50,173,230), C(60,60,70) };
            for (int i = 0; i < 9; i++) Rect(10.75f + (i % 3) * 10, 10.75f + (i / 3) * 10, 6.5f, 6.5f, 1.5f, style==LaunchpadIconStyle.WhiteSquares?C(255,255,255):style==LaunchpadIconStyle.ColorOnWhite&&i==5?C(180,180,190):colors[i]);
        }
        else
        {
            Rect(12, 13, 24, 4, 2, C(255, 255, 255));
            Rect(12, 22, 18, 4, 2, C(255, 255, 255));
            Rect(12, 31, 24, 4, 2, C(255, 255, 255));
        }
        _fallback = visual; _content.Children.InsertAtBottom(visual);
    }

    public void Dispose()
    {
        _disposed = true; _generation++;
        _brush.Surface = null; _surface?.Dispose(); _fallback?.Dispose();
        _image.Dispose(); _brush.Dispose(); _content.Dispose(); _pulse.Dispose();Visual.Dispose();
    }
}

