namespace FloatingDock.Core.Geometry;

/// <summary>
/// Maths of the continuous "fisheye" magnification. The whole row is warped by X(x) = x + A * BellInt(x - u) where BellInt is the integral
/// of a smooth bell. X is strictly increasing (slope 1 + A*bell &gt;= 1), X(u) = u, and positions/sizes are smooth functions of the cursor,
/// so nothing jumps when the cursor crosses the gap between two icons and no feedback from the already-magnified layout exists.
/// </summary>
public static class Magnification
{
    private static readonly float E4 = MathF.Exp(-4f);
    private static readonly float ErfTwo = Erf(2f);

    /// <summary>Bell 0..1: (exp(-4 (t/R)^2) - e^-4) / (1 - e^-4) for |t| &lt; R, 0 beyond.</summary>
    public static float Bell(float t, float radius)
    {
        var a = MathF.Abs(t);
        if (a >= radius) return 0f;
        var r = a / radius;
        return (MathF.Exp(-4f * r * r) - E4) / (1f - E4);
    }

    /// <summary>Integral of <see cref="Bell"/> from 0 to t (odd function, constant beyond +-radius).</summary>
    public static float BellInt(float t, float radius)
    {
        var a = MathF.Min(MathF.Abs(t), radius);
        // Most icons are outside the hover radius. Its saturated integral is
        // constant; preserve the same arithmetic without re-evaluating exp/erf.
        var erf = a == radius ? ErfTwo : Erf(2f * a / radius);
        var v = (radius * 0.44311346f * erf - E4 * a) / (1f - E4);
        return t < 0 ? -v : v;
    }

    /// <summary>Error function, |error| &lt; 1.2e-7 (Numerical Recipes erfc Chebyshev fit).</summary>
    public static float Erf(float x)
    {
        double z = Math.Abs((double)x);
        if (z < 0.5)
        {
            double z2 = z * z;
            double series = z * (1.0 - z2 / 3.0 * (1.0 - z2 * 3.0 / 10.0 * (1.0 - z2 * 5.0 / 21.0 * (1.0 - z2 * 7.0 / 36.0 * (1.0 - z2 * 9.0 / 55.0))))) * 1.1283791670955126;
            return (float)(x >= 0 ? series : -series);
        }
        double t = 1.0 / (1.0 + 0.5 * z);
        double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                     t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        double erf = 1.0 - ans;
        return (float)(x >= 0 ? erf : -erf);
    }

    public static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Easing of the zoom envelope: smoothness 0 = linear, 5 = smoothstep, 10 = smootherstep (very soft start/stop).</summary>
    public static float EaseEnvelope(float t, int smoothness)
    {
        t = Math.Clamp(t, 0f, 1f);
        float lin = t;
        float s3 = t * t * (3f - 2f * t);
        float s5 = t * t * t * (t * (t * 6f - 15f) + 10f);
        float k = Math.Clamp(smoothness, 0, 10) / 10f;
        return k <= 0.5f ? lin + (s3 - lin) * (k / 0.5f) : s3 + (s5 - s3) * ((k - 0.5f) / 0.5f);
    }

    /// <summary>Strength factor so that the icon directly under the cursor reaches exactly the configured scale.</summary>
    public static float Normalisation(float iconSize, float radius)
    {
        float mean = 2f * BellInt(iconSize * 0.5f, radius) / iconSize;
        return mean > 0.05f ? MathF.Min(1f / mean, 1.5f) : 1.5f;
    }
}

/// <summary>Hover / zoom state: cursor smoothing and the eased zoom envelope. Advances on real elapsed time, never per frame count.</summary>
public sealed class MagnificationState
{
    public bool Hovering { get; set; }
    public float Zoom { get; private set; }
    public float CursorX { get; private set; }
    private float _linear;
    private bool _hasCursor;

    public void Reset() { Hovering = false; _linear = 0; Zoom = 0; _hasCursor = false; }

    /// <summary>Sets the raw cursor position (window-local DIP).</summary>
    public void SetCursor(float x, bool snap)
    {
        if (!_hasCursor || snap) { CursorX = x; _hasCursor = true; }
        _target = x;
    }
    private float _target;

    public bool IsSettled => !Hovering && _linear <= 0f;

    /// <summary>Returns true while something is still moving (the caller keeps its frame loop alive).</summary>
    public bool Advance(float dtSeconds, int responsiveness, int animationSpeed, int smoothness)
    {
        dtSeconds = Math.Clamp(dtSeconds, 0f, 0.1f);
        float seconds = 0.45f - (Math.Clamp(animationSpeed, 1, 10) - 1) * (0.36f / 9f);   // 450 ms .. 90 ms
        float step = dtSeconds / seconds;
        _linear = Hovering ? MathF.Min(1f, _linear + step) : MathF.Max(0f, _linear - step);
        Zoom = Magnification.EaseEnvelope(_linear, smoothness);

        bool moving = false;
        if (Hovering)
        {
            if (responsiveness >= 10) { moving = CursorX != _target; CursorX = _target; }
            else
            {
                float rate = 6f + (responsiveness - 1) * (54f / 9f);          // 1/s
                float k = 1f - MathF.Exp(-rate * dtSeconds);
                CursorX += (_target - CursorX) * k;
                if (MathF.Abs(_target - CursorX) < 0.05f) CursorX = _target; else moving = true;
            }
        }
        return (Hovering ? _linear < 1f : _linear > 0f) || moving;
    }
}
