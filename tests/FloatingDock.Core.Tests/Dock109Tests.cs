using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.Core.Icons;
using Xunit;

namespace FloatingDock.Core.Tests;
public class Dock109Tests
{
    private static WindowInfo W(long h,string exe)=>new(h,7,exe,null,"Steam","Chrome_WidgetWin_1",false);
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe")]
    [InlineData(@"D:\Steam\bin\cef\cef.win64\steamwebhelper.exe")]
    public void PinnedSteamClaimsItsClientHelper(string helper)
    {
        string client=helper[..helper.IndexOf(@"\bin\cef\",StringComparison.Ordinal)]+@"\steam.exe";
        var pin=new AppRef { Name="Steam",ExePath=client };
        var entries=new DockEntryReconciler().Build(new(false,false,true,true),new[]{pin},new[]{W(1,helper)},1);
        var entry=Assert.Single(entries);Assert.True(entry.IsPinned);Assert.True(entry.IsActive);Assert.Single(entry.Windows);
    }
    [Fact] public void UnpinnedSteamClientAndHelpersShareOneSlot()
    {
        var entries=new DockEntryReconciler().Build(new(false,false,true,true),Array.Empty<AppRef>(),new[]{W(1,@"D:\Steam\steam.exe"),W(2,@"D:\Steam\bin\cef\win64\steamwebhelper.exe")},2);
        var entry=Assert.Single(entries);Assert.Equal(2,entry.Windows.Count);Assert.Equal(@"d:\steam\steam.exe",entry.App!.ExePath);
    }
    [Fact] public void SteamGamesAreSeparateApplications()
    {
        var pin=new AppRef { Name="Steam",ExePath=@"D:\Steam\steam.exe" };
        Assert.False(AppMatching.Matches(pin,W(1,@"D:\Steam\steamapps\common\Game\game.exe")));
    }
    [Fact] public void DifferentSteamInstallationsDoNotMerge()
    {
        var pin=new AppRef { Name="Steam",ExePath=@"C:\Steam\steam.exe" };
        Assert.False(AppMatching.Matches(pin,W(1,@"D:\Steam\bin\cef\win64\steamwebhelper.exe")));
    }
    [Fact] public void UnrelatedProcessNamedSteamwebhelperIsNotRewritten()
    {
        Assert.Equal(@"c:\other\steamwebhelper.exe",AppMatching.CanonicalExePath(@"C:\Other\steamwebhelper.exe"));
    }
    [Fact] public void PinningTheHelperAndClientDoesNotDuplicate()
    {
        var entries=new DockEntryReconciler().Build(new(false,false,true,true),new[]{new AppRef { ExePath=@"D:\Steam\steam.exe" },new AppRef { ExePath=@"D:\Steam\bin\cef\win64\steamwebhelper.exe" }},new[]{W(1,@"D:\Steam\bin\cef\win64\steamwebhelper.exe")},1);
        Assert.Single(entries);Assert.Single(entries[0].Windows);
    }
}
