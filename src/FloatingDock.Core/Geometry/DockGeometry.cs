using FloatingDock.Core.Configuration;

namespace FloatingDock.Core.Geometry;

/// <summary>Immutable numeric inputs of the layout (DIP), derived from <see cref="DockSettings"/>.</summary>
public sealed record GeometryParams(
    float IconSize, float Gap, float DockHeight, bool FixedWidth, float FixedWidthValue,
    DockAlignment Alignment, float MaxScale, float Radius, float SeparatorWidth, float ShadowMargin, float LabelHeadroom)
{
    public float VPad => (DockHeight - IconSize) * 0.5f;
    public float PadX => VPad;
    // Reserve space above a 5px indicator including its 1.25 active scale.
    public float IconBottomInset => MathF.Max(VPad, 12f);

    public static GeometryParams From(DockSettings s) => new(
        s.IconSize, s.IconSpacing, s.DockHeight, s.WidthMode == WidthMode.Fixed, s.FixedWidth, s.Alignment,
        s.MagnificationEnabled ? s.MaxScalePercent / 100f : 1f, s.MagnificationRadius, 10f, 28f, s.ShowLabels ? 34f : 0f);
}

/// <summary>
/// Static window geometry: computed only from the item <i>set</i> and the settings, never from the cursor or the current magnification,
/// so the window (and the hover zone) never move while icons grow.
/// </summary>
public sealed class WindowMetrics
{
    public float WindowWidth { get; init; }
    public float WindowHeight { get; init; }
    /// <summary>Plate of the resting dock at full presence of all items (window-local).</summary>
    public float RestPlateLeft { get; init; }
    public float RestPlateRight { get; init; }
    public float PlateTop { get; init; }
    public float PlateBottom { get; init; }
    /// <summary>Extra room on each side that the widened (magnified) plate may need.</summary>
    public float Overshoot { get; init; }
    public float ShadowMargin { get; init; }
    /// <summary>Hover entry zone: resting plate + a little slack. Stable by construction.</summary>
    public Rect EnterZone { get; init; }
    /// <summary>Hover keep zone: entry zone + headroom for the magnified icons (still static).</summary>
    public Rect KeepZone { get; init; }

    public static float Span(GeometryParams p, IReadOnlyList<(float Presence, bool IsSeparator)> items)
    {
        float span = 0, maxPresence = 0;
        foreach (var (pe, sep) in items)
        {
            float w = sep ? p.SeparatorWidth : p.IconSize;
            span += (w + p.Gap) * pe;
            maxPresence = MathF.Max(maxPresence,pe);
        }
        return Math.Max(0f, span - maxPresence*p.Gap);
    }

    /// <param name="items">All items including the ones that are still fading out, evaluated at presence 1.</param>
    public static WindowMetrics Compute(GeometryParams p, int itemCount, int separatorCount,float? viewportWidth=null)
    {
        var list = new List<(float, bool)>(itemCount);
        for (int i = 0; i < itemCount; i++) list.Add((1f, i < separatorCount));
        float span = Span(p, list);
        float plateW = p.FixedWidth ? MathF.Max(p.FixedWidthValue, span + 2 * p.PadX) : span + 2 * p.PadX;
        float A = (p.MaxScale - 1f) * Magnification.Normalisation(p.IconSize, p.Radius);
        float overshoot = MathF.Ceiling(A * Magnification.BellInt(p.Radius, p.Radius)) + 2f;
        if (p.MaxScale <= 1.0001f) overshoot = 2f;
        // A caller can keep the native viewport stable while the visible plate
        // changes size. Layout and input zones still follow the actual items.
        float winW = viewportWidth is float width ? MathF.Max(1,width) : plateW + 2 * (overshoot + p.ShadowMargin);
        float headroom = MathF.Max(0f, p.IconSize * p.MaxScale - (p.DockHeight - p.IconBottomInset)) + p.LabelHeadroom + 6f;
        float winH = p.DockHeight + headroom + p.ShadowMargin * 2f;
        float plateB = winH - p.ShadowMargin;
        float plateT = plateB - p.DockHeight;
        float restL = p.Alignment switch
        {
            DockAlignment.Left when !p.FixedWidth => p.ShadowMargin + overshoot,
            DockAlignment.Right when !p.FixedWidth => winW - p.ShadowMargin - overshoot - plateW,
            _ => (winW - plateW) * 0.5f,
        };
        var restR = restL + plateW;
        var enter = new Rect(restL - 4f, plateT, plateW + 8f, p.DockHeight + p.ShadowMargin);
        float hoverTop=MathF.Max(0,plateB-p.IconBottomInset-p.IconSize*p.MaxScale-8);
        var keep = new Rect(restL - 12f - overshoot * 0.5f, hoverTop, plateW + 24f + overshoot, winH-hoverTop);
        return new WindowMetrics
        {
            WindowWidth = MathF.Ceiling(winW), WindowHeight = MathF.Ceiling(winH),
            RestPlateLeft = restL, RestPlateRight = restR, PlateTop = plateT, PlateBottom = plateB,
            Overshoot = overshoot, ShadowMargin = p.ShadowMargin, EnterZone = enter, KeepZone = keep,
        };
    }
}

public readonly record struct Rect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;
    public Rect Inflate(float d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}
