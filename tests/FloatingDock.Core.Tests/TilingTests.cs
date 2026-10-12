using FloatingDock.Core.Configuration;
using FloatingDock.Core.Tiling;
using Xunit;

namespace FloatingDock.Core.Tests;
public sealed class TilingTests
{
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
