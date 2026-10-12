using FloatingDock.Core.Configuration;
using FloatingDock.Core.Geometry;
using FloatingDock.Core.Models;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class Dock119Tests
{
    private static DockEntry Entry(string key,DockEntryKind kind=DockEntryKind.App)=>new() { Key=key,Name=key,Kind=kind };

    [Fact]
    public void ZeroPresenceTrailingSlotsDoNotAddAGap()
    {
        var p=GeometryParams.From(new DockSettings().Normalize());var motion=new DockMotion();
        motion.SetEntries(new[]{Entry("a"),Entry("b")},false);var m=WindowMetrics.Compute(p,4,1);
        var before=DockLayout.Compute(p,m,motion.Items,0,0);float width=before.PlateRight-before.PlateLeft,iconX=motion.Items[0].X;
        motion.SetEntries(new[]{Entry("a"),Entry("b"),Entry("separator",DockEntryKind.Separator),Entry("c")},true);
        var after=DockLayout.Compute(p,m,motion.Items,0,0);
        Assert.Equal(width,after.PlateRight-after.PlateLeft,3);Assert.Equal(iconX,motion.Items[0].X,3);
    }

    [Fact]
    public void RemovingZeroPresenceGhostsDoesNotSnapPlateWidth()
    {
        var p=GeometryParams.From(new DockSettings().Normalize());var motion=new DockMotion();
        motion.SetEntries(new[]{Entry("a"),Entry("b"),Entry("c")},false);
        motion.SetEntries(new[]{Entry("a"),Entry("b")},true);motion.Items[2].Appear=0;
        var m=WindowMetrics.Compute(p,3,0);var before=DockLayout.Compute(p,m,motion.Items,0,0);float width=before.PlateRight-before.PlateLeft;
        motion.Advance(0,new DockSettings());var after=DockLayout.Compute(p,m,motion.Items,0,0);
        Assert.Equal(width,after.PlateRight-after.PlateLeft,3);
    }

    [Theory]
    [InlineData(DockAlignment.Left)]
    [InlineData(DockAlignment.Center)]
    [InlineData(DockAlignment.Right)]
    public void StableViewportKeepsExistingIconsContinuousAcrossNewShape(DockAlignment alignment)
    {
        var s=new DockSettings { Alignment=alignment,IconAnimationMs=260 }.Normalize();
        var p=GeometryParams.From(s);var motion=new DockMotion();
        motion.SetEntries(new[]{Entry("a"),Entry("b")},false);
        var before=WindowMetrics.Compute(p,2,0,1200);DockLayout.Compute(p,before,motion.Items,0,0);float oldX=motion.Items[0].X;
        motion.SetEntries(new[]{Entry("a"),Entry("b"),Entry("sep",DockEntryKind.Separator),Entry("c")},true);
        var after=WindowMetrics.Compute(p,4,1,1200);DockLayout.Compute(p,after,motion.Items,0,0);
        Assert.Equal(before.WindowWidth,after.WindowWidth);Assert.Equal(oldX,motion.Items[0].X,3);
        for(int i=0;i<30;i++)motion.Advance(.016f,s);
        motion.SetEntries(new[]{Entry("a"),Entry("b")},true);
        for(int i=0;i<30;i++)motion.Advance(.016f,s);
        var restored=WindowMetrics.Compute(p,2,0,1200);DockLayout.Compute(p,restored,motion.Items,0,0);
        Assert.Equal(oldX,motion.Items[0].X,3);
    }

    [Theory]
    [InlineData(0f,.3f,1f)]
    [InlineData(1f,0f,1f)]
    [InlineData(.8f,.2f,0f)]
    [InlineData(0f,0f,0f)]
    public void WeightedSpanMatchesActualItemExtents(float first,float second,float third)
    {
        var p=GeometryParams.From(new DockSettings().Normalize());
        var items=new[]{new MotionItem { Key="a",Entry=Entry("a"),Appear=first },new MotionItem { Key="b",Entry=Entry("b"),Appear=second },new MotionItem { Key="c",Entry=Entry("c"),Appear=third }};
        float span=WindowMetrics.Span(p,items.Select(i=>(i.Presence,i.IsSeparator)).ToArray());
        var result=DockLayout.Compute(p,WindowMetrics.Compute(p,3,0,1000),items,0,0);
        Assert.Equal(span+2*p.PadX,result.PlateRight-result.PlateLeft,3);
    }
}
