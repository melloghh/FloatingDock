using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FloatingDock.UI.Views;

/// <summary>
/// The Material 3 expressive progress indicator: the played part is a travelling wave, the rest a flat rounded track.
/// The wave is one pre-built path scrolled by a looping compositor animation, so a tick only changes a clip width.
/// </summary>
internal sealed class WavySlider : Grid
{
    private const double Period = 22, Amplitude = 4.2, Stroke = 4, Pitch = 26;
    private readonly Border _track = new() { Height = Stroke, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _played = new() { HorizontalAlignment = HorizontalAlignment.Left, Height = Pitch };
    private readonly Microsoft.UI.Xaml.Shapes.Path _wave = new() { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Ellipse _knob = new() { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private double _progress;
    private bool _moving;

    internal WavySlider()
    {
        Height = Pitch; MinWidth = 120;
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new global::Windows.Foundation.Point(0, Pitch / 2) };
        double half = Period / 2; int halves = 40;
        for (int i = 0; i < halves; i++)
        {
            double x = i * half, dir = i % 2 == 0 ? -1 : 1;
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new global::Windows.Foundation.Point(x + half * .36, Pitch / 2 + dir * Amplitude * 1.33),
                Point2 = new global::Windows.Foundation.Point(x + half * .64, Pitch / 2 + dir * Amplitude * 1.33),
                Point3 = new global::Windows.Foundation.Point(x + half, Pitch / 2),
            });
        }
        geometry.Figures.Add(figure);
        _wave.Data = geometry; _wave.Width = half * halves; _wave.Height = Pitch;
        _played.Clip = new RectangleGeometry();
        _played.Children.Add(_wave);
        Children.Add(_track); Children.Add(_played); Children.Add(_knob);
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => { ApplyColors(); StartWave(); };
        Unloaded += (_, _) => _moving = false;
    }

    internal void ApplyColors()
    {
        var c = M3.Current;
        _track.Background = M3.Brush(c.ContainerHighest);
        _wave.Stroke = M3.Brush(c.Primary);
        _knob.Fill = M3.Brush(c.Primary);
    }

    /// <summary>Fraction played, 0..1. A paused track draws a flat line (the wave only travels while playing).</summary>
    internal void Set(double fraction, bool playing)
    {
        _progress = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        Layout();
        if (playing != _moving) { _moving = playing; StartWave(); }
    }

    private void Layout()
    {
        double width = ActualWidth; if (width <= 0) return;
        double played = Math.Max(Stroke, width * _progress);
        _played.Width = played;
        ((RectangleGeometry)_played.Clip).Rect = new global::Windows.Foundation.Rect(0, 0, played, Pitch);
        _track.Margin = new Thickness(Math.Min(width, played + 4), 0, 0, 0);
        _knob.Margin = new Thickness(Math.Min(width - 8, played - 2), 0, 0, 0);
    }

    private void StartWave()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_wave, true);
        var visual = ElementCompositionPreview.GetElementVisual(_wave);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!_moving || !SurfaceMotion.Enabled) return;
        var compositor = visual.Compositor;
        using var linear = compositor.CreateLinearEasingFunction();
        using var scroll = compositor.CreateVector3KeyFrameAnimation();
        scroll.Duration = TimeSpan.FromMilliseconds(1100); scroll.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        scroll.InsertKeyFrame(0, Vector3.Zero); scroll.InsertKeyFrame(1, new Vector3((float)-Period, 0, 0), linear);
        visual.StartAnimation("Translation", scroll);
    }
}
