using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class Dock118Tests
{
    [Fact]
    public void AppOnlyReorderPreservesSeparatorSlotsAndPersistsAppOrder()
    {
        using var dir=new TempDir();var path=dir.File("pins.json");var store=new AppListStore(path);
        var a=new AppRef { Name="A" };var b=new AppRef { Name="B" };var c=new AppRef { Name="C" };
        var first=AppRef.Separator();var second=AppRef.Separator();
        store.ReplaceAll(new[]{a,first,b,second,c});int changed=0;store.Changed+=()=>changed++;
        Assert.True(store.MoveApplication(c.Key,0));
        Assert.Equal(new[]{c.Key,first.Key,a.Key,second.Key,b.Key},new AppListStore(path).Snapshot().Select(p=>p.Key));
        Assert.True(store.MoveApplication(c.Key,2));
        Assert.Equal(new[]{a.Key,first.Key,b.Key,second.Key,c.Key},store.Snapshot().Select(p=>p.Key));
        Assert.Equal(2,changed);
    }

    [Fact]
    public void AppOnlyReorderRefusesSeparatorMissingKeyAndNoOp()
    {
        using var dir=new TempDir();var store=new AppListStore(dir.File("pins.json"));
        var a=new AppRef { Name="A" };var separator=AppRef.Separator();
        store.ReplaceAll(new[]{separator,a});int changed=0;store.Changed+=()=>changed++;
        Assert.False(store.MoveApplication(separator.Key,0));
        Assert.False(store.MoveApplication("missing",0));
        Assert.False(store.MoveApplication(a.Key,100));
        Assert.Equal(new[]{separator.Key,a.Key},store.Snapshot().Select(p=>p.Key));Assert.Equal(0,changed);
    }

    [Fact]
    public void AppOnlyReorderClampsIndicesWithinVisibleApps()
    {
        using var dir=new TempDir();var store=new AppListStore(dir.File("pins.json"));
        var a=new AppRef { Name="A" };var b=new AppRef { Name="B" };var separator=AppRef.Separator();
        store.ReplaceAll(new[]{separator,a,b});
        Assert.True(store.MoveApplication(a.Key,int.MaxValue));
        Assert.Equal(new[]{separator.Key,b.Key,a.Key},store.Snapshot().Select(p=>p.Key));
        Assert.True(store.MoveApplication(a.Key,int.MinValue));
        Assert.Equal(new[]{separator.Key,a.Key,b.Key},store.Snapshot().Select(p=>p.Key));
    }

    [Fact]
    public void FailedAppOnlyReorderRollsBackAndReportsFailure()
    {
        using var dir=new TempDir();var path=dir.File("pins.json");var store=new AppListStore(path);
        var a=new AppRef { Name="A" };var b=new AppRef { Name="B" };var separator=AppRef.Separator();
        store.ReplaceAll(new[]{a,separator,b});File.Delete(path);Directory.CreateDirectory(path);
        int changed=0,failed=0;store.Changed+=()=>changed++;store.SaveFailed+=_=>failed++;
        Assert.False(store.MoveApplication(b.Key,0));
        Assert.Equal(new[]{a.Key,separator.Key,b.Key},store.Snapshot().Select(p=>p.Key));
        Assert.NotNull(store.LastError);Assert.Equal(0,changed);Assert.Equal(1,failed);
    }
}
