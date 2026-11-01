using System.Diagnostics;
using System.Numerics;
using FloatingDock.UI.Views;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FloatingDock.UI.MenuBar;

/// <summary>
/// The "pop" of every floating surface: it springs out of its anchor with a small overshoot and settles.
/// It only drives compositor animations of the XAML panel. The window is transparent and larger than the panel
/// by <see cref="Pop.PadDip"/>, so the overshoot is never cut off, and no window region or backdrop is animated.
/// </summary>
internal sealed class MenuSurfaceMotion : IDisposable
{
    private readonly Microsoft.UI.Composition.Visual _visual;
    private readonly float _from;
    private long _started;
    private int _duration;
    private float _fromProgress, _toProgress;
    private bool _running;
    private Action? _completed;
    private int _generation;
    internal float Progress => _running ? _fromProgress + (_toProgress - _fromProgress) * Curve(Math.Clamp((float)(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / Math.Max(1, _duration)), 0, 1)) : _toProgress;
    internal bool IsRunning => _running;
    internal (int L, int T, int R, int B, int Radius)? Clip => null;
    internal bool VisualMatchesProgressForDiagnostics => true;

    /// <param name="panel">The visible surface (not the padded window root).</param>
    internal MenuSurfaceMotion(IntPtr hwnd, FrameworkElement panel, GlassBackdrop? glass = null, float minX = .86f, float minY = .86f, float lift = -10, int radius = 16)
    {
        _from = Math.Min(minX, minY);
        ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
        _visual = ElementCompositionPreview.GetElementVisual(panel);
    }

    private static float Curve(float t) => 1 - (1 - t) * (1 - t) * (1 - t);

    /// <summary>The surface grows from this point (panel-relative DIPs, usually the anchor's centre on the panel's top edge).</summary>
    internal void SetGeometry(int width, int height, double dpi, double pivot, double pivotY = 0) => _visual.CenterPoint = new Vector3((float)Math.Max(0, pivot), (float)Math.Max(0, pivotY), 0);
    internal void SetSplit(int height, int gap) { }

    private void Pose(float progress)
    {
        float s = _from + (1 - _from) * progress;
        _visual.StopAnimation("Scale"); _visual.StopAnimation("Opacity"); _visual.StopAnimation("Translation");
        _visual.Scale = new Vector3(s, s, 1);
        _visual.Opacity = Math.Clamp(progress * 3, 0, 1);
        _visual.Properties.InsertVector3("Translation", new Vector3(0, -10 * (1 - progress), 0));
    }

    internal void Prepare(float progress)
    {
        Stop(); _fromProgress = _toProgress = Math.Clamp(progress, 0, 1);
        Pose(_toProgress);
    }

    internal void Play(bool entering, int milliseconds, Action? completed = null)
    {
        float current = Progress;
        Stop(); _completed = completed;
        _fromProgress = current; _toProgress = entering ? 1 : 0;
        if (!SurfaceMotion.Enabled || Math.Abs(_toProgress - _fromProgress) < .0001f)
        {
            Pose(_toProgress); Finish(); return;
        }
        var compositor = _visual.Compositor;
        float travel = Math.Abs(_toProgress - _fromProgress);
        _duration = Math.Max(90, (int)(milliseconds * Math.Max(.35f, travel)));
        _started = Stopwatch.GetTimestamp(); _running = true;
        int generation = ++_generation;
        var span = TimeSpan.FromMilliseconds(_duration);
        using var decelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1f));
        using var accelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(.4f, 0f), new Vector2(1f, 1f));
        float startScale = _from + (1 - _from) * current;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        using var scale = compositor.CreateVector3KeyFrameAnimation();
        using var fade = compositor.CreateScalarKeyFrameAnimation();
        using var lift = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = fade.Duration = lift.Duration = span;
        scale.InsertKeyFrame(0, new Vector3(startScale, startScale, 1));
        fade.InsertKeyFrame(0, Math.Clamp(current * 3, 0, 1));
        lift.InsertKeyFrame(0, new Vector3(0, -10 * (1 - current), 0));
        if (entering)
        {
            // Springs past rest by ~4 % at 62 % of the time, then settles.
            scale.InsertKeyFrame(.62f, new Vector3(1.04f, 1.04f, 1), decelerate);
            scale.InsertKeyFrame(1, new Vector3(1, 1, 1), decelerate);
            fade.InsertKeyFrame(.35f, 1, decelerate); fade.InsertKeyFrame(1, 1);
            lift.InsertKeyFrame(1, Vector3.Zero, decelerate);
        }
        else
        {
            float end = _from + .06f;
            scale.InsertKeyFrame(1, new Vector3(end, end, 1), accelerate);
            fade.InsertKeyFrame(1, 0, accelerate);
            lift.InsertKeyFrame(1, new Vector3(0, -6, 0), accelerate);
        }
        _visual.StartAnimation("Scale", scale); _visual.StartAnimation("Opacity", fade); _visual.StartAnimation("Translation", lift);
        batch.End();
        batch.Completed += (_, _) => { if (generation == _generation) Finish(); batch.Dispose(); };
    }

    private void Finish()
    {
        _running = false;
        var completed = _completed; _completed = null; completed?.Invoke();
    }

    internal void Stop()
    {
        // Reversing keeps the last pose: stop without resetting, the next Play starts from the current progress.
        float p = Progress; _generation++;
        _visual.StopAnimation("Scale"); _visual.StopAnimation("Opacity"); _visual.StopAnimation("Translation");
        _running = false; _fromProgress = _toProgress = p; _completed = null;
    }

    public void Dispose() => Stop();
}
