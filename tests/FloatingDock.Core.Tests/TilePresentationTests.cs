using FloatingDock.Core.Tiling;
using Xunit;

namespace FloatingDock.Core.Tests;
public sealed class TilePresentationTests
{
    [Fact] public void InterruptedReflowPreservesPositionAndSizeMomentumAndSettles()
    {
        var from=new TileRect(-1600,60,700,800);var first=new TileMotion(from,new(-900,80,950,600),0,300,interpolateSize:true);
        var sample=first.Sample(90);
        var target=new TileRect(-400,120,1100,500);
        var resumed=new TileMotion(sample.Bounds,target,90,220,sample.VelocityX,sample.VelocityY,true,sample.VelocityWidth,sample.VelocityHeight);
        var continued=resumed.Sample(90);Assert.Equal(sample.Bounds,continued.Bounds);
        Assert.Equal(sample.VelocityX,continued.VelocityX,10);Assert.Equal(sample.VelocityY,continued.VelocityY,10);
        Assert.Equal(sample.VelocityWidth,continued.VelocityWidth,10);Assert.Equal(sample.VelocityHeight,continued.VelocityHeight,10);
        int width=sample.Bounds.Width,height=sample.Bounds.Height;
        for(int i=1;i<=220;i++)
        {
            var next=resumed.Sample(90+i);Assert.InRange(next.Bounds.Width,width,target.Width);Assert.InRange(next.Bounds.Height,target.Height,height);
            width=next.Bounds.Width;height=next.Bounds.Height;
        }
        Assert.Equal(target,resumed.Sample(310).Bounds);Assert.True(resumed.Complete(310));
    }
    [Fact] public void UntiledMotionStartsGentlyAndOnlyShrinksTowardsTheIcon()
    {
        var from=new TileRect(1500,80,960,600);var dock=new TileRect(20,600,40,40);
        var first=TileMinimizeMotion.Sample(from,dock,.01,true);
        Assert.InRange(Math.Abs(first.CenterX-from.CenterX),0,2);
        int width=from.Width,height=from.Height;
        for(int i=1;i<=100;i++)
        {
            var r=TileMinimizeMotion.Sample(from,dock,i/100d,true);
            Assert.True(r.Width<=width&&r.Height<=height);width=r.Width;height=r.Height;
        }
        Assert.Equal(TileMinimizeMotion.FitAtDock(from,dock),TileMinimizeMotion.Sample(from,dock,1,true));
    }
    [Fact] public void UnknownBrowserAndTallPlayerReflowWithoutASliver()
    {
        var tree=new TileTree();var bounds=new TileRect(0,0,1920,1048);
        var minima=new Dictionary<long,(int Width,int Height)>{{1,(480,600)},{2,(120,80)},{3,TileMinimum.Resolve(0,0)},{4,(800,600)}};
        for(int i=1;i<=4;i++)Assert.True(tree.AddBalanced(i,i-1,bounds,8,12,minima.Where(p=>p.Key<=i).ToDictionary()));
        var layout=tree.Layout(bounds);Assert.Equal(4,layout.Count);Assert.True(layout[3].Width>=480);Assert.True(layout[3].Height>=320);
        foreach(var a in layout)foreach(var b in layout.Where(b=>b.Key!=a.Key))Assert.True(a.Value.X+a.Value.Width<=b.Value.X||b.Value.X+b.Value.Width<=a.Value.X||a.Value.Y+a.Value.Height<=b.Value.Y||b.Value.Y+b.Value.Height<=a.Value.Y);
        Assert.Equal((720,480),TileMinimum.Resolve(0,0,1.5));Assert.Equal((800,600),TileMinimum.Resolve(800,600));
    }
    [Theory]
    [InlineData(0,500)]
    [InlineData(1850,500)]
    [InlineData(960,1040)]
    public void MinimizationKeepsProportionsAndFinishesAtTheIcon(int dockX,int dockY)
    {
        var from=new TileRect(200,80,960,600);var dock=new TileRect(dockX,dockY,40,40);var final=TileMinimizeMotion.Sample(from,dock,1);
        Assert.Equal(from,TileMinimizeMotion.Sample(from,dock,0));Assert.InRange(Math.Abs(final.CenterX-dock.CenterX),0,.5);Assert.InRange(Math.Abs(final.CenterY-dock.CenterY),0,.5);
        foreach(var t in Enumerable.Range(0,101))
        {
            var r=TileMinimizeMotion.Sample(from,dock,t/100d);Assert.InRange(r.Width,1,from.Width);Assert.InRange(r.Height,1,from.Height);
            Assert.InRange(Math.Abs(r.Width-r.Height*from.Width/(double)from.Height),0,1.5);
        }
    }
}
