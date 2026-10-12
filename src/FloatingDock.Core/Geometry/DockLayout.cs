using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;

namespace FloatingDock.Core.Geometry;

public sealed class DockLayoutResult
{
    public float PlateLeft { get; set; }
    public float PlateRight { get; set; }
    public float PlateTop { get; set; }
    public float PlateBottom { get; set; }
    public float ItemsTop { get; set; }
}

/// <summary>
/// Positions every <see cref="MotionItem"/> and the plate for the current frame. Stateless apart from the outputs it writes:
/// the target of the zoom is always computed from the <i>un-magnified</i> row, so there is no feedback loop.
/// </summary>
public static class DockLayout
{
    public static DockLayoutResult Compute(GeometryParams p, WindowMetrics m, IReadOnlyList<MotionItem> items, float cursorX, float zoom, DockLayoutResult? result = null)
    {
        var res = result ?? new DockLayoutResult();
        res.PlateTop = m.PlateTop; res.PlateBottom = m.PlateBottom;
        float pitchIcon = p.IconSize + p.Gap;
        float span = 0f, lastPe = 0f;
        foreach (var it in items)
        {
            float pe = it.Presence;
            float w = it.IsSeparator ? p.SeparatorWidth : p.IconSize;
            span += (w + p.Gap) * pe;
            lastPe = pe;
        }
        if (items.Count > 0) span -= p.Gap * lastPe;
        span = MathF.Max(0f, span);

        float plateW = p.FixedWidth ? MathF.Max(p.FixedWidthValue, span + 2 * p.PadX) : span + 2 * p.PadX;
        float baseL, baseW = plateW;
        if (p.FixedWidth || p.Alignment == DockAlignment.Center) baseL = (m.WindowWidth - plateW) * 0.5f;
        else if (p.Alignment == DockAlignment.Left) baseL = m.ShadowMargin + m.Overshoot;
        else baseL = m.WindowWidth - m.ShadowMargin - m.Overshoot - plateW;
        float L0 = p.FixedWidth || p.Alignment == DockAlignment.Center ? baseL + (baseW - span) * 0.5f
                 : p.Alignment == DockAlignment.Left ? baseL + p.PadX : baseL + baseW - p.PadX - span;

        float R = MathF.Max(1f, p.Radius);
        float A = (p.MaxScale - 1f) * Magnification.Normalisation(p.IconSize, R) * zoom;
        bool warp = A > 0.0005f;
        float iconBottom = m.PlateBottom - p.IconBottomInset;

        float itemsL = float.MaxValue, itemsR = float.MinValue, itemsT = float.MaxValue;
        float x = L0;
        foreach (var it in items)
        {
            float pe = it.Presence;
            float w = (it.IsSeparator ? p.SeparatorWidth : p.IconSize) * pe;
            float b = x;
            float l = b, r = b + w;
            if (warp) { l += A * Magnification.BellInt(l - cursorX, R); r += A * Magnification.BellInt(r - cursorX, R); }
            x += ((it.IsSeparator ? p.SeparatorWidth : p.IconSize) + p.Gap) * pe;

            if (it.PendingSlide && it.HasLastBase) { it.Slide = it.LastBaseX - b; }
            it.PendingSlide = false;
            it.LastBaseX = b;
            it.HasLastBase = true;
            it.BaseX = b;

            float size = r - l;
            float px = l + it.Slide;
            if (it.DragX is float dx) px = dx - size * 0.5f;
            it.X = px;
            it.Size = size;
            it.Bottom = iconBottom;
            itemsL = MathF.Min(itemsL, px);
            itemsR = MathF.Max(itemsR, px + size);
            if (!it.IsSeparator) itemsT = MathF.Min(itemsT, iconBottom - size);
        }

        if (items.Count == 0 || itemsL > itemsR) { itemsL = baseL + p.PadX; itemsR = baseL + baseW - p.PadX; }
        if (p.FixedWidth)
        {
            res.PlateLeft = MathF.Min(baseL, itemsL - p.PadX);
            res.PlateRight = MathF.Max(baseL + baseW, itemsR + p.PadX);
        }
        else
        {
            res.PlateLeft = itemsL - p.PadX;
            res.PlateRight = itemsR + p.PadX;
        }
        res.ItemsTop = itemsT == float.MaxValue ? m.PlateTop : itemsT;
        return res;
    }

    /// <summary>Index of the non-separator item under (x,y), or -1. Gaps belong to the nearest icon, so there are no dead pixels between icons.</summary>
    public static int HitTest(IReadOnlyList<MotionItem> items, GeometryParams p, WindowMetrics m, float x, float y)
    {
        if (y < m.PlateTop - (p.IconSize * (p.MaxScale - 1f)) || y > m.PlateBottom + 2f) return -1;
        int best = -1;
        float half = p.Gap * 0.5f;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.IsSeparator || it.Leaving || it.Presence < 0.3f) continue;
            if (x >= it.X - half && x < it.X + it.Size + half)
            {
                // above the plate only the icon itself counts
                if (y < m.PlateTop && y < it.Top) return -1;
                best = i;
                break;
            }
        }
        return best;
    }
}
