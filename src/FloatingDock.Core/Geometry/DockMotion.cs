using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;

namespace FloatingDock.Core.Geometry;

/// <summary>Visual state of one dock slot (including slots that are fading out). Separate from the model: see <see cref="DockEntry"/>.</summary>
public sealed class MotionItem
{
    public required string Key { get; init; }
    public DockEntry Entry { get; set; } = null!;
    public bool Leaving { get; set; }
    /// <summary>0..1 linear progress of appear/disappear; eased with <see cref="Magnification.Smooth01"/> when used.</summary>
    public float Appear { get; set; }
    public float RunningAlpha { get; set; }
    public float ActiveAlpha { get; set; }
    public float Slide { get; set; }
    public float LastBaseX { get; set; }
    public bool HasLastBase { get; set; }
    public bool PendingSlide { get; set; }
    public bool IsSeparator => Entry.Kind == DockEntryKind.Separator;
    public float Presence => Magnification.Smooth01(Appear);
    // ---- outputs of the latest layout ----
    public float BaseX { get; set; }
    public float X { get; set; }
    public float Size { get; set; }
    public float Bottom { get; set; }
    public float Top => Bottom - Size;
    /// <summary>When dragging, the item follows the cursor instead of its slot.</summary>
    public float? DragX { get; set; }
}

/// <summary>Owns the ordered list of <see cref="MotionItem"/> and advances appear / disappear / slide animations on elapsed time.</summary>
public sealed class DockMotion
{
    private readonly List<MotionItem> _items = new();

    public IReadOnlyList<MotionItem> Items => _items;
    public int LiveCount => _items.Count(i => !i.Leaving);

    /// <summary>Items that exist at presence 1 if nothing was leaving: used for the stable window metrics.</summary>
    public (int Count, int Separators) MetricsShape()
    {
        int n = 0, s = 0;
        foreach (var i in _items) { n++; if (i.IsSeparator) s++; }
        return (n, s);
    }

    public void SetEntries(IReadOnlyList<DockEntry> entries, bool animate)
    {
        var old = _items.ToList();
        var oldByKey = old.ToDictionary(i => i.Key);
        var newKeys = new HashSet<string>(entries.Select(e => e.Key));

        // ghosts: old items that are gone, remembered after the surviving item that preceded them
        var ghostsAfter = new Dictionary<string, List<MotionItem>>();
        var leading = new List<MotionItem>();
        string? lastSurvivor = null;
        foreach (var o in old)
        {
            if (newKeys.Contains(o.Key)) { lastSurvivor = o.Key; continue; }
            if (!animate || o.Presence <= 0.001f) continue;                 // nothing visible to animate out
            o.Leaving = true;
            if (lastSurvivor is null) leading.Add(o);
            else
            {
                if (!ghostsAfter.TryGetValue(lastSurvivor, out var l)) ghostsAfter[lastSurvivor] = l = new();
                l.Add(o);
            }
        }

        bool sameSet = old.Count(o => !o.Leaving) == entries.Count && entries.All(e => oldByKey.TryGetValue(e.Key, out var o) && !o.Leaving);
        bool orderChanged = false;
        if (sameSet)
        {
            var oldOrder = old.Where(o => !o.Leaving).Select(o => o.Key).ToList();
            for (int i = 0; i < oldOrder.Count; i++) if (oldOrder[i] != entries[i].Key) { orderChanged = true; break; }
        }

        var result = new List<MotionItem>(entries.Count + 4);
        result.AddRange(leading);
        foreach (var e in entries)
        {
            if (!oldByKey.TryGetValue(e.Key, out var item))
            {
                item = new MotionItem { Key = e.Key, Entry = e, Appear = animate ? 0f : 1f };
                item.RunningAlpha = e.IsRunning ? (animate ? 0f : 1f) : 0f;
                item.ActiveAlpha = e.IsActive ? 1f : 0f;
            }
            else
            {
                item.Entry = e;
                item.Leaving = false;
                if (orderChanged && animate) item.PendingSlide = true;
            }
            result.Add(item);
            if (ghostsAfter.TryGetValue(e.Key, out var g)) result.AddRange(g);
        }
        _items.Clear();
        _items.AddRange(result);
        if (!animate) foreach (var i in _items) i.Appear = 1f;
    }

    /// <summary>Advances every animation by <paramref name="dt"/> seconds. Returns true while anything is still moving.</summary>
    public bool Advance(float dt, DockSettings s)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        bool animating = false;
        float dur = (s.IconAnimation ? s.IconAnimationMs : 1) / 1000f;
        float step = dt / MathF.Max(dur, 0.001f);
        float fadeStep = dt / 0.18f;
        float slideK = 1f - MathF.Exp(-14f * dt);

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            float target = it.Leaving ? 0f : 1f;
            if (it.Appear != target)
            {
                it.Appear = it.Appear < target ? MathF.Min(target, it.Appear + step) : MathF.Max(target, it.Appear - step);
                animating = true;
            }
            if (it.Leaving && it.Appear <= 0f) { _items.RemoveAt(i); continue; }

            float rt = it.Entry.IsRunning && !it.Leaving ? 1f : 0f;
            if (it.RunningAlpha != rt) { it.RunningAlpha = Step(it.RunningAlpha, rt, fadeStep); animating = true; }
            float at = it.Entry.IsActive && !it.Leaving ? 1f : 0f;
            if (it.ActiveAlpha != at) { it.ActiveAlpha = Step(it.ActiveAlpha, at, fadeStep); animating = true; }
            if (MathF.Abs(it.Slide) > 0.05f) { it.Slide -= it.Slide * slideK; animating = true; } else it.Slide = 0f;
        }
        return animating;
    }

    private static float Step(float v, float target, float step) =>
        v < target ? MathF.Min(target, v + step) : MathF.Max(target, v - step);
}
