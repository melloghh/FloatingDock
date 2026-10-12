using FloatingDock.Core.Configuration;
using FloatingDock.Core.Tiling;
using Xunit;

namespace FloatingDock.Core.Tests;
public sealed class TilingTests
{
    [Fact] public void RestoringAnExistingTallMemberReflowsItsMinimumAgain()
    {
        var tree=new TileTree();var bounds=new TileRect(0,0,1920,1080);var minima=new Dictionary<long,(int Width,int Height)>{{1,(480,600)},{2,(800,600)},{3,(400,250)},{4,(480,400)}};
        for(int i=1;i<=4;i++)tree.AddBalanced(i,i-1,bounds,8,12,minima.Where(p=>p.Key<=i&&p.Key!=2).ToDictionary());
        Assert.True(tree.AddBalanced(2,0,bounds,8,12,minima));
        var layout=tree.Layout(bounds);Assert.Equal(4,layout.Count);Assert.All(layout,p=>{Assert.True(p.Value.Width>=minima[p.Key].Width);Assert.True(p.Value.Height>=minima[p.Key].Height);});
    }
    [Fact] public void TallPlayerReflowsFourMixedWindowsInsteadOfCoveringTwoTiles()
    {
        var tree=new TileTree();var bounds=new TileRect(0,32,1920,1048);
        var minimums=new Dictionary<long,(int Width,int Height)>{{1,(480,600)},{2,(400,250)},{3,(480,400)}};
        for(int i=1;i<=3;i++)Assert.True(tree.AddBalanced(i,i-1,bounds,8,12,minimums.Where(p=>p.Key<=i).ToDictionary()));
        minimums[4]=(800,600);Assert.True(tree.AddBalanced(4,3,bounds,8,12,minimums));
        var layout=tree.Layout(bounds);Assert.Equal(4,layout.Count);
        foreach(var (id,r) in layout)
        {Assert.True(r.Width>=minimums[id].Width);Assert.True(r.Height>=minimums[id].Height);Assert.InRange(r.X,12,1920-r.Width-12);Assert.InRange(r.Y,44,1080-r.Height-12);}
        foreach(var a in layout)foreach(var b in layout.Where(b=>b.Key!=a.Key))
            Assert.True(a.Value.X+a.Value.Width<=b.Value.X||b.Value.X+b.Value.Width<=a.Value.X||a.Value.Y+a.Value.Height<=b.Value.Y||b.Value.Y+b.Value.Height<=a.Value.Y);
    }
    [Fact] public void UnequalMinimumHeightsAdjustSplitWithoutDroppingAnyWindow()
    {
        var tree=new TileTree();var bounds=new TileRect(0,0,1920,1080);var minimums=new Dictionary<long,(int Width,int Height)>{{1,(600,600)},{2,(800,600)},{3,(800,300)}};
        for(int i=1;i<=3;i++)Assert.True(tree.AddBalanced(i,i-1,bounds,8,12,minimums.Where(p=>p.Key<=i).ToDictionary()));
        var layout=tree.Layout(bounds);Assert.Equal(3,layout.Count);Assert.True(layout[2].Height>=600);Assert.True(layout[3].Height>=300);
    }
    [Fact] public void ThreeBalancedWindowsKeepUsefulWidthInsteadOfQuarterWidthColumns()
    {
        var tree=new TileTree();var bounds=new TileRect(0,0,1920,1080);
        tree.AddBalanced(1,0,bounds);tree.AddBalanced(2,1,bounds);tree.AddBalanced(3,2,bounds);
        var layout=tree.Layout(bounds);Assert.Equal(3,layout.Count);
        Assert.All(layout.Values,r=>{Assert.True(r.Width>900);Assert.True(r.Height>480);});
        Assert.Equal(layout[2].X,layout[3].X);Assert.True(layout[3].Y>layout[2].Y);
    }
    [Fact] public void RetargetPreservesPositionAndVelocityAndSettlesWithoutOvershoot()
    {
        var path=new TileMotion(new(0,0,400,300),new(900,600,400,300),0,220);
        Assert.Equal(0,path.Sample(0).VelocityX);Assert.True(path.Sample(16).Bounds.X<10);
        var current=path.Sample(90);var next=new TileMotion(current.Bounds,new(1200,800,400,300),90,220,current.VelocityX,current.VelocityY);
        Assert.Equal(current.Bounds,next.Sample(90).Bounds);Assert.Equal(current.VelocityX,next.Sample(90).VelocityX,7);
        Assert.Equal(next.Target,next.Sample(310).Bounds);Assert.Equal(0,next.Sample(310).VelocityX);
        Assert.All(Enumerable.Range(0,221).Select(t=>next.Sample(90+t).Bounds),r=>Assert.InRange(r.X,current.Bounds.X,1200));
    }
    [Fact] public void ThreeTallAppsReflowIntoColumnsWhenNeitherLeafSplitFits()
    {
        var tree=new TileTree();var bounds=new TileRect(0,0,1920,1080);var minimums=new Dictionary<long,(int Width,int Height)>();
        for(int i=1;i<=3;i++){minimums[i]=(600,700);tree.AddBalanced(i,i-1,bounds,8,12,minimums);}
        var layout=tree.Layout(bounds);Assert.Equal(3,layout.Count);
        Assert.All(layout.Values,r=>{Assert.True(r.Width>=600);Assert.True(r.Height>=700);});
        Assert.Equal(new long[]{1,2,3},tree.Windows);
    }
    [Fact] public void AnImpossibleNewTileDoesNotEvictOrResizeTheExistingTiles()
    {
        var tree=new TileTree();var bounds=new TileRect(0,0,1200,800);var minimums=new Dictionary<long,(int Width,int Height)>{{1,(560,750)},{2,(560,750)}};
        Assert.True(tree.AddBalanced(1,0,bounds,8,12,minimums));Assert.True(tree.AddBalanced(2,1,bounds,8,12,minimums));
        var before=tree.Layout(bounds);minimums[3]=(560,750);Assert.False(tree.AddBalanced(3,2,bounds,8,12,minimums));
        Assert.Equal(new long[]{1,2},tree.Windows);Assert.Equal(before[1],tree.Layout(bounds)[1]);Assert.Equal(before[2],tree.Layout(bounds)[2]);
    }
    [Fact] public void StartupWaitsForQuietGeometryButHasAFiniteDeadline()
    {
        var gate=new WindowArrivalGate();var rect=new TileRect(0,0,400,300);
        Assert.False(gate.Ready(1,rect,0,180));Assert.False(gate.Ready(1,rect with{Width=500},160,180));
        Assert.False(gate.Ready(1,rect with{Width=500},230,180));Assert.True(gate.Ready(1,rect with{Width=500},260,180));
        for(int time=0;time<700;time+=50)Assert.False(gate.Ready(2,rect with{X=time},time,180));
        Assert.True(gate.Ready(2,rect with{X=700},700,180));
    }
    [Fact] public void ShortcutOverridesCloneIndependentlyAndRejectEquivalentDuplicates()
    {
        var settings=new DockSettings();settings.TilingKeybindings["focus.left"]="Alt+Left";
        var clone=settings.Clone();clone.TilingKeybindings["focus.left"]="Alt+Right";
        Assert.Equal("Alt+Left",settings.TilingKeybindings["focus.left"]);Assert.Null(TilingShortcuts.Error(settings));
        settings.TilingKeybindings["focus.right"]="alt+left";Assert.NotNull(TilingShortcuts.Error(settings));
        settings.TilingKeybindings["focus.right"]="";Assert.Null(TilingShortcuts.Error(settings));
        settings.TilingKeybindings["focus.left"]=settings.SearchHotkey;Assert.NotNull(TilingShortcuts.Error(settings));
    }
    [Fact] public void NestedSplitsNeverOverlapAndCollapseAfterRemoval()
    {
        var tree=new TileTree();tree.Add(1);tree.Add(2,1);tree.Add(3,2,false);
        var layout=tree.Layout(new(-1920,0,1920,1080));
        Assert.Equal(3,layout.Count);Assert.Equal(layout[2].X,layout[3].X);Assert.True(layout[2].Y+layout[2].Height<layout[3].Y);
        Assert.True(layout[1].X+layout[1].Width<layout[2].X);
        Assert.True(tree.Remove(2));Assert.Equal(2,tree.Layout(new(0,0,1920,1080)).Count);
        tree.Remove(3);Assert.Equal(new TileRect(12,12,1896,1056),tree.Layout(new(0,0,1920,1080))[1]);
    }
    [Fact] public void DirectionFocusSwapResizeAndOrientationKeepIdentities()
    {
        var tree=new TileTree();tree.Add(1);tree.Add(2);var bounds=new TileRect(0,0,1200,800);
        var before=tree.Layout(bounds);Assert.Equal(2,TileTree.Neighbor(1,TileDirection.Right,before));Assert.Equal(0,TileTree.Neighbor(1,TileDirection.Left,before));
        tree.Resize(1,TileDirection.Right);Assert.True(tree.Layout(bounds)[1].Width>before[1].Width);
        tree.Swap(1,2);Assert.True(tree.Layout(bounds)[1].X>tree.Layout(bounds)[2].X);
        tree.ToggleDirection(1);Assert.Equal(tree.Layout(bounds)[1].X,tree.Layout(bounds)[2].X);
        Assert.Equal(new long[]{2,1},tree.Windows);tree.Add(1);Assert.Equal(2,tree.Windows.Count);
    }
    [Fact] public void MinimizedWindowsDoNotLeaveEmptyTiles()
    {
        var tree=new TileTree();tree.Add(1);tree.Add(2);tree.Add(3,2,false);
        var visible=new HashSet<long>{3};var layout=tree.Layout(new(0,0,1200,800),8,12,visible);
        Assert.Single(layout);Assert.Equal(new TileRect(12,12,1176,776),layout[3]);
        Assert.Equal(3,tree.Windows.Count);
    }
    [Fact] public void SettingsAreSafeByDefaultAndRemoveDocumentsFromExistingProfiles()
    {
        var defaults=new DockSettings();Assert.False(defaults.TilingEnabled);
        var s=new DockSettings {SearchFolders="Desktop;Documents;Downloads",SearchDepth=500,TilingGap=500,TilingWorkspaces=99,TilingAnimationMs=-1,TilingModifier=(TilingModifier)99}.Normalize();
        Assert.Equal("Desktop;Downloads",s.SearchFolders);Assert.Equal(3,s.SearchDepth);Assert.Equal(9,s.TilingWorkspaces);Assert.Equal(40,s.TilingGap);Assert.Equal(0,s.TilingAnimationMs);Assert.Equal(TilingModifier.Alt,s.TilingModifier);
    }
}
