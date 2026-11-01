using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace FloatingDock.UI.Views;

/// <summary>
/// A rounded highlight that glides from row to row instead of each row flashing on its own. Put <see cref="Layer"/>
/// behind the rows (first child of a Grid that also holds them); it only draws through a composition shape, so it
/// never takes part in layout or input.
/// </summary>
internal sealed class GlideHighlight
{
    private ShapeVisual? _shape;
    private CompositionRoundedRectangleGeometry? _geometry;
    private CompositionColorBrush? _brush;
    private bool _placed;
    private global::Windows.UI.Color _color = global::Windows.UI.Color.FromArgb(40, 255, 255, 255);
    private (Vector2 Offset, Vector2 Size, float Radius)? _pending;
    internal Grid Layer { get; } = new() { IsHitTestVisible = false };

    internal GlideHighlight()
    {
        Layer.Loaded += (_, _) => { Ensure(); if (_pending is { } p) { _pending = null; Place(p.Offset, p.Size, p.Radius, animate: false); } };
        Layer.SizeChanged += (_, e) => { if (_shape is not null) _shape.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height); };
    }

    private void Ensure()
    {
        if (_shape is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(Layer).Compositor;
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _brush = compositor.CreateColorBrush(_color);
        var sprite = compositor.CreateSpriteShape(_geometry);
        sprite.FillBrush = _brush;
        _shape = compositor.CreateShapeVisual();
        _shape.Shapes.Add(sprite);
        _shape.Size = new Vector2((float)Math.Max(1, Layer.ActualWidth), (float)Math.Max(1, Layer.ActualHeight));
        _shape.Opacity = 0;
        ElementCompositionPreview.SetElementChildVisual(Layer, _shape);
    }

    internal void SetColor(global::Windows.UI.Color color)
    {
        _color = color;
        if (_brush is not null) _brush.Color = color;
    }

    /// <summary>Glides the highlight to <paramref name="target"/> (which must be a descendant of the layer's parent).</summary>
    internal void MoveTo(FrameworkElement target, double radius = 16, bool animate = true)
    {
        if (target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
        var origin = target.TransformToVisual(Layer).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        var offset = new Vector2((float)origin.X, (float)origin.Y);
        var size = new Vector2((float)target.ActualWidth, (float)target.ActualHeight);
        if (_shape is null) { _pending = (offset, size, (float)radius); return; }
        Place(offset, size, (float)radius, animate && SurfaceMotion.Enabled);
    }

    private void Place(Vector2 offset, Vector2 size, float radius, bool animate)
    {
        if (_shape is null || _geometry is null) return;
        _geometry.CornerRadius = new Vector2(radius);
        if (!_placed || !animate)
        {
            _geometry.StopAnimation("Offset"); _geometry.StopAnimation("Size");
            _geometry.Offset = offset; _geometry.Size = size;
            _shape.StopAnimation("Opacity");
            if (!_placed && animate)
            {
                var fade = _shape.Compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(160); fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1);
                _shape.StartAnimation("Opacity", fade);
            }
            else _shape.Opacity = 1;
            _placed = true; return;
        }
        var compositor = _shape.Compositor;
        using var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .9f), new Vector2(.25f, 1f));
        using var move = compositor.CreateVector2KeyFrameAnimation();
        using var grow = compositor.CreateVector2KeyFrameAnimation();
        var span = TimeSpan.FromMilliseconds(380);
        move.Duration = grow.Duration = span;
        move.InsertExpressionKeyFrame(0, "this.StartingValue"); move.InsertKeyFrame(1, offset, ease);
        grow.InsertExpressionKeyFrame(0, "this.StartingValue"); grow.InsertKeyFrame(1, size, ease);
        _geometry.StartAnimation("Offset", move); _geometry.StartAnimation("Size", grow);
        if (_shape.Opacity < 1) _shape.Opacity = 1;
    }

    internal void Hide()
    {
        _placed = false; _pending = null;
        if (_shape is null) return;
        _shape.StopAnimation("Opacity"); _shape.Opacity = 0;
    }
}
