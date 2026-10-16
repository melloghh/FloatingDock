using FloatingDock.Core.Configuration;
using FloatingDock.Core.Geometry;
using FloatingDock.Core.Models;
using Xunit;

namespace FloatingDock.Core.Tests;

public class MagnificationTests
{
    [Fact]
    public void Erf_MatchesKnownValues()
    {
        Assert.Equal(0f, Magnification.Erf(0f), 1e-6f);
        Assert.Equal(0.8427008f, Magnification.Erf(1f), 1e-5f);
        Assert.Equal(-0.9953223f, Magnification.Erf(-2f), 1e-5f);
    }

    [Fact]
    public void BellInt_IsOddMonotonicAndMatchesNumericIntegral()
    {
        const float R = 150f;
        Assert.Equal(0f, Magnification.BellInt(0, R));
        Assert.Equal(-Magnification.BellInt(37f, R), Magnification.BellInt(-37f, R), 1e-4f);
        float prev = -1f;
        for (float t = 0; t <= 200; t += 1)
        {
            var v = Magnification.BellInt(t, R);
            Assert.True(v >= prev - 1e-4f);
            prev = v;
        }
        // numeric integral of Bell
        double sum = 0; const int n = 20000;
        for (int i = 0; i < n; i++) sum += Magnification.Bell((i + 0.5f) * R / n, R) * (R / n);
        Assert.Equal((float)sum, Magnification.BellInt(R, R), 0.05f);
        Assert.Equal(Magnification.BellInt(R, R), Magnification.BellInt(R * 3, R), 1e-3f);   // constant beyond the radius
    }

    [Fact]
    public void SaturatedIntegral_PreservesOriginalFloatResults()
    {
        float e4=MathF.Exp(-4f);
        for(int radius=1;radius<=1024;radius++)
        {
            float original=(radius*0.44311346f*Magnification.Erf(2f*radius/radius)-e4*radius)/(1f-e4);
            Assert.Equal(original,Magnification.BellInt(radius,radius));
            Assert.Equal(original,Magnification.BellInt(radius*3f,radius));
            Assert.Equal(-original,Magnification.BellInt(-radius*3f,radius));
        }
    }

    [Fact]
    public void WarpIsStrictlyIncreasing_SoIconsNeverOverlapOrReorder()
    {
        const float R = 150f, A = 0.9f;
        float u = 333f, prev = float.MinValue;
        for (float x = 0; x < 800; x += 0.5f)
        {
            float X = x + A * Magnification.BellInt(x - u, R);
            Assert.True(X > prev);
            prev = X;
        }
        Assert.Equal(u, u + A * Magnification.BellInt(0, R));    // the point under the cursor stays under it
    }

    [Fact]
    public void EaseEnvelope_EndpointsAndMonotonic()
    {
        foreach (var s in new[] { 0, 5, 10 })
        {
            Assert.Equal(0f, Magnification.EaseEnvelope(0, s), 1e-6f);
            Assert.Equal(1f, Magnification.EaseEnvelope(1, s), 1e-6f);
            float prev = 0;
            for (float t = 0; t <= 1f; t += 0.01f) { var v = Magnification.EaseEnvelope(t, s); Assert.True(v >= prev - 1e-6f); prev = v; }
        }
    }

    [Fact]
    public void MagnificationState_ZoomsInAndOutAndSettles()
    {
        var st = new MagnificationState { Hovering = true };
        st.SetCursor(100, true);
        for (int i = 0; i < 100; i++) st.Advance(0.016f, 8, 6, 5);
        Assert.Equal(1f, st.Zoom, 1e-4f);
        st.Hovering = false;
        bool active = true;
        for (int i = 0; i < 200 && active; i++) active = st.Advance(0.016f, 8, 6, 5);
        Assert.False(active);
        Assert.Equal(0f, st.Zoom);
        Assert.True(st.IsSettled);
    }

    [Fact]
    public void MagnificationState_CursorFollowsWithoutOvershoot()
    {
        var st = new MagnificationState { Hovering = true };
        st.SetCursor(0, true);
        st.SetCursor(100, false);
        float last = 0;
        for (int i = 0; i < 200; i++) { st.Advance(0.016f, 5, 6, 5); Assert.True(st.CursorX >= last - 1e-4f && st.CursorX <= 100.0001f); last = st.CursorX; }
        Assert.Equal(100f, st.CursorX, 0.1f);
    }

    [Fact]
    public void StationaryHover_SettlesWithoutRemovingMagnification_AndMovesAgain()
    {
        var state = new MagnificationState { Hovering = true };
        state.SetCursor(100, true);
        for (int i = 0; i < 100; i++) state.Advance(.016f, 10, 6, 5);
        Assert.False(state.Advance(.016f, 10, 6, 5));
        Assert.Equal(1, state.Zoom);
        state.SetCursor(140, false);
        Assert.True(state.Advance(.016f, 10, 6, 5));
        Assert.Equal(140, state.CursorX);
        Assert.False(state.Advance(.016f, 10, 6, 5));
    }

    [Fact]
    public void SmoothedHover_RemainsActiveUntilCursorHasSettled()
    {
        var state = new MagnificationState { Hovering = true };
        state.SetCursor(0, true);
        for (int i = 0; i < 100; i++) state.Advance(.016f, 4, 6, 5);
        state.SetCursor(100, false);
        Assert.True(state.Advance(.016f, 4, 6, 5));
        for (int i = 0; i < 300; i++) state.Advance(.016f, 4, 6, 5);
        Assert.False(state.Advance(.016f, 4, 6, 5));
        Assert.Equal(100, state.CursorX);
    }
}

public class LayoutTests
{
    private static (GeometryParams P, DockMotion M) Make(int count, DockSettings? s = null)
    {
        s ??= new DockSettings().Normalize();
        var motion = new DockMotion();
        var entries = Enumerable.Range(0, count).Select(i => new DockEntry { Kind = DockEntryKind.App, Key = "k" + i, Name = "n" + i }).ToList();
        motion.SetEntries(entries, animate: false);
        return (GeometryParams.From(s), motion);
    }

    [Fact]
    public void RestLayout_IsCenteredAndEvenlySpaced()
    {
        var (p, motion) = Make(5);
        var m = WindowMetrics.Compute(p, 5, 0);
        var r = DockLayout.Compute(p, m, motion.Items, 0, 0f);
        var mid = (r.PlateLeft + r.PlateRight) / 2;
        Assert.Equal(m.WindowWidth / 2, mid, 1f);
        for (int i = 1; i < 5; i++) Assert.Equal(p.IconSize + p.Gap, motion.Items[i].X - motion.Items[i - 1].X, 0.01f);
        Assert.All(motion.Items, i => Assert.Equal(p.IconSize, i.Size, 0.01f));
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(12)] [InlineData(25)]
    public void MagnifiedLayout_NeverLeavesTheWindow_ForAnyCursorPosition(int count)
    {
        foreach (var align in new[] { DockAlignment.Center, DockAlignment.Left, DockAlignment.Right })
        {
            var s = new DockSettings { Alignment = align, MaxScalePercent = 220, MagnificationRadius = 200 }.Normalize();
            var (p, motion) = Make(count, s);
            var m = WindowMetrics.Compute(p, count, 0);
            for (float cx = -50; cx < m.WindowWidth + 50; cx += 7)
            {
                var r = DockLayout.Compute(p, m, motion.Items, cx, 1f);
                Assert.True(r.PlateLeft >= 0 && r.PlateRight <= m.WindowWidth, $"plate outside window: {r.PlateLeft}..{r.PlateRight} / {m.WindowWidth} (align {align}, cursor {cx})");
                foreach (var it in motion.Items)
                {
                    Assert.True(it.X >= 0 && it.X + it.Size <= m.WindowWidth);
                    Assert.True(it.Top >= 0, "magnified icon is cut at the top");
                }
            }
        }
    }

    [Fact]
    public void IconUnderCursor_ReachesConfiguredMaxScale_AndOrderIsPreserved()
    {
        var s = new DockSettings { MaxScalePercent = 170, MagnificationRadius = 150 }.Normalize();
        var (p, motion) = Make(10, s);
        var m = WindowMetrics.Compute(p, 10, 0);
        DockLayout.Compute(p, m, motion.Items, 0, 0f);
        var center = motion.Items[4].X + p.IconSize / 2;
        DockLayout.Compute(p, m, motion.Items, center, 1f);
        var item = motion.Items[4];
        Assert.InRange(item.Size / p.IconSize, 1.6f, 1.75f);
        for (int i = 1; i < motion.Items.Count; i++) Assert.True(motion.Items[i].X >= motion.Items[i - 1].X + motion.Items[i - 1].Size - 0.01f);
    }

    [Fact]
    public void NoJump_WhenCursorCrossesGapBetweenIcons()
    {
        var (p, motion) = Make(8);
        var m = WindowMetrics.Compute(p, 8, 0);
        float prevSize = -1, prevX = 0;
        for (float cx = 100; cx < 200; cx += 0.25f)
        {
            DockLayout.Compute(p, m, motion.Items, cx, 1f);
            var it = motion.Items[3];
            if (prevSize >= 0) { Assert.True(MathF.Abs(it.Size - prevSize) < 0.8f); Assert.True(MathF.Abs(it.X - prevX) < 1.5f); }
            prevSize = it.Size; prevX = it.X;
        }
    }

    [Fact]
    public void WindowMetrics_DoNotDependOnMagnificationOrCursor()
    {
        var (p, _) = Make(6);
        var a = WindowMetrics.Compute(p, 6, 0);
        var b = WindowMetrics.Compute(p, 6, 0);
        Assert.Equal(a.WindowWidth, b.WindowWidth);
        Assert.Equal(a.KeepZone, b.KeepZone);
        Assert.True(a.KeepZone.Contains(a.WindowWidth / 2, a.PlateTop - 20));       // headroom is hoverable once the zoom is running
        Assert.False(a.EnterZone.Contains(a.WindowWidth / 2, a.PlateTop - 20));    // but does not start it
    }

    [Fact]
    public void HitTest_HasNoDeadPixelsBetweenIcons()
    {
        var (p, motion) = Make(6);
        var m = WindowMetrics.Compute(p, 6, 0);
        DockLayout.Compute(p, m, motion.Items, 0, 0f);
        float y = m.PlateBottom - p.VPad - p.IconSize / 2;
        int last = -1;
        for (float x = motion.Items[0].X; x < motion.Items[5].X + p.IconSize; x += 0.5f)
        {
            var hit = DockLayout.HitTest(motion.Items, p, m, x, y);
            Assert.True(hit >= 0, $"dead pixel at {x}");
            Assert.True(hit >= last); last = hit;
        }
        Assert.Equal(-1, DockLayout.HitTest(motion.Items, p, m, motion.Items[5].X + p.IconSize + 40, y));
    }

    [Fact]
    public void FixedWidth_KeepsPlateWidthAndCentersItems()
    {
        var s = new DockSettings { WidthMode = WidthMode.Fixed, FixedWidth = 900 }.Normalize();
        var (p, motion) = Make(3, s);
        var m = WindowMetrics.Compute(p, 3, 0);
        var r = DockLayout.Compute(p, m, motion.Items, 0, 0f);
        Assert.Equal(900f, r.PlateRight - r.PlateLeft, 0.5f);
    }
}

public class MotionTests
{
    private static DockEntry E(string key, bool running = false, DockEntryKind kind = DockEntryKind.App) =>
        new() { Kind = kind, Key = key, Name = key, Windows = running ? new[] { new WindowInfo(1, 1, "c:\\a.exe", null, "t", "c", false) } : Array.Empty<WindowInfo>() };

    [Fact]
    public void NewItem_GrowsIn_AndRemovedItem_ShrinksOut_ThenDisappears()
    {
        var s = new DockSettings { IconAnimationMs = 200 }.Normalize();
        var motion = new DockMotion();
        motion.SetEntries(new[] { E("a"), E("b") }, false);
        motion.SetEntries(new[] { E("a"), E("b"), E("c") }, true);
        Assert.Equal(0f, motion.Items[2].Presence);
        for (int i = 0; i < 30; i++) motion.Advance(0.016f, s);
        Assert.Equal(1f, motion.Items[2].Presence);

        motion.SetEntries(new[] { E("a"), E("c") }, true);
        Assert.Equal(3, motion.Items.Count);                  // "b" is still there, leaving
        Assert.True(motion.Items[1].Leaving);
        motion.Advance(0.05f, s);
        Assert.InRange(motion.Items[1].Presence, 0.01f, 0.99f);
        for (int i = 0; i < 40; i++) motion.Advance(0.016f, s);
        Assert.Equal(2, motion.Items.Count);
        Assert.DoesNotContain(motion.Items, i => i.Key == "b");
    }

    [Fact]
    public void RemovedItem_StaysAtItsPlace_BetweenNeighbours()
    {
        var motion = new DockMotion();
        motion.SetEntries(new[] { E("a"), E("b"), E("c"), E("d") }, false);
        motion.SetEntries(new[] { E("a"), E("d") }, true);
        Assert.Equal(new[] { "a", "b", "c", "d" }, motion.Items.Select(i => i.Key).ToArray());
        Assert.True(motion.Items[1].Leaving && motion.Items[2].Leaving);
    }

    [Fact]
    public void ReAddedWhileLeaving_ComesBack()
    {
        var s = new DockSettings().Normalize();
        var motion = new DockMotion();
        motion.SetEntries(new[] { E("a"), E("b") }, false);
        motion.SetEntries(new[] { E("a") }, true);
        motion.Advance(0.05f, s);
        motion.SetEntries(new[] { E("a"), E("b") }, true);
        Assert.False(motion.Items[1].Leaving);
        for (int i = 0; i < 40; i++) motion.Advance(0.016f, s);
        Assert.Equal(1f, motion.Items[1].Presence);
    }

    [Fact]
    public void RunningIndicator_FadesWithoutRemovingPinnedItem()
    {
        var s = new DockSettings().Normalize();
        var motion = new DockMotion();
        motion.SetEntries(new[] { E("a", running: true) }, false);
        motion.Items[0].RunningAlpha = 1f;
        motion.SetEntries(new[] { E("a", running: false) }, true);
        Assert.False(motion.Items[0].Leaving);
        motion.Advance(0.05f, s);
        Assert.InRange(motion.Items[0].RunningAlpha, 0.01f, 0.99f);
        for (int i = 0; i < 30; i++) motion.Advance(0.016f, s);
        Assert.Equal(0f, motion.Items[0].RunningAlpha);
        Assert.Single(motion.Items);
    }

    [Fact]
    public void Reorder_CreatesSlide_ThatDecays()
    {
        var s = new DockSettings().Normalize();
        var p = GeometryParams.From(s);
        var motion = new DockMotion();
        motion.SetEntries(new[] { E("a"), E("b"), E("c") }, false);
        var m = WindowMetrics.Compute(p, 3, 0);
        DockLayout.Compute(p, m, motion.Items, 0, 0);
        motion.SetEntries(new[] { E("b"), E("a"), E("c") }, true);
        DockLayout.Compute(p, m, motion.Items, 0, 0);
        Assert.NotEqual(0f, motion.Items.First(i => i.Key == "a").Slide);
        for (int i = 0; i < 120; i++) motion.Advance(0.016f, s);
        Assert.Equal(0f, motion.Items.First(i => i.Key == "a").Slide);
    }

    [Fact]
    public void Advance_ReturnsFalseAtRest_SoTheFrameLoopCanStop()
    {
        var s = new DockSettings().Normalize();
        var motion = new DockMotion();
        motion.SetEntries(new[] { E("a"), E("b") }, true);
        int guard = 0;
        while (motion.Advance(0.016f, s) && guard++ < 1000) { }
        Assert.True(guard < 1000);
        Assert.False(motion.Advance(0.016f, s));
    }
}
