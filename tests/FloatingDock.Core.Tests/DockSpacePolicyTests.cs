using FloatingDock.Core.Configuration;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class DockSpacePolicyTests
{
    private static DockSettings Settings(bool keep=false,bool reserve=false,bool hide=true,TaskbarMode taskbar=TaskbarMode.HideCompletely,bool autoHide=false)
        => new(){KeepDockInFullscreen=keep,ReserveWorkArea=reserve,HideOnFullscreen=hide,TaskbarMode=taskbar,AutoHide=autoHide};

    [Fact]
    public void DefaultKeepsPreviousBehaviour()
    {
        var s=new DockSettings();
        Assert.False(s.KeepDockInFullscreen);
        Assert.False(DockSpacePolicy.ShouldReserve(s,false));
        Assert.True(DockSpacePolicy.HidesForCoveringWindow(s,false));
        Assert.False(DockSpacePolicy.ShouldFitFullscreen(s,false));
    }

    [Fact]
    public void KeepModeReservesTheStripAndDoesNotHide()
    {
        var s=Settings(keep:true);
        Assert.True(DockSpacePolicy.ShouldReserve(s,false));
        Assert.False(DockSpacePolicy.HidesForCoveringWindow(s,false));
        Assert.True(DockSpacePolicy.ShouldFitFullscreen(s,false));
    }

    [Fact]
    public void ExclusiveFullscreenGamesStillHideTheDock()
    {
        var s=Settings(keep:true);
        Assert.True(DockSpacePolicy.HidesForCoveringWindow(s,true));
        Assert.False(DockSpacePolicy.ShouldFitFullscreen(s,true));
    }

    [Theory]
    [InlineData(TaskbarMode.Leave,false,false)]
    [InlineData(TaskbarMode.HideCompletely,true,false)]
    [InlineData(TaskbarMode.AutoHide,false,true)]
    public void KeepModeNeedsAHiddenTaskbarAndAVisibleDock(TaskbarMode taskbar,bool autoHide,bool expected)
        => Assert.Equal(expected&&!autoHide,DockSpacePolicy.ShouldReserve(Settings(keep:true,taskbar:taskbar,autoHide:autoHide),false));

    [Fact]
    public void HiddenDockReservesNothing() => Assert.False(DockSpacePolicy.ShouldReserve(Settings(keep:true),true));

    [Theory]
    [InlineData(true,true,true)]
    [InlineData(true,false,false)]
    [InlineData(false,true,false)]
    public void VideoHidesTheDockInKeepMode(bool keep,bool video,bool expected)
        => Assert.Equal(expected,DockSpacePolicy.HidesForVideo(Settings(keep:keep),video));
}
