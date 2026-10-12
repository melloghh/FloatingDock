using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;
public sealed class PaletteTests
{
    [Theory]
    [InlineData("Деинсталлировать Telegram",null,null)]
    [InlineData("Live captions",null,"MicrosoftWindows.Client.CBS_cw5n1h2txyewy!LiveCaptions")]
    [InlineData("Run",@"C:\Windows\System32\rundll32.exe",null)]
    [InlineData("Voice access",@"C:\Windows\SystemApps\VoiceAccess\VoiceAccess.exe",null)]
    [InlineData("Windows backup",null,"Microsoft.WindowsBackup_8wekyb3d8bbwe!App")]
    public void SystemUtilitiesAndUninstallersAreExcluded(string name,string? exe,string? aumid)
    {Assert.False(PaletteAppPolicy.Include(new(){Name=name,ExePath=exe,Aumid=aumid,Source=AppSource.AppsFolder}));}
    [Fact] public void FreshSuggestionsAreEmptyAndSuccessfulUsesPersistInFrequencyOrder()
    {
        var a=new AppRef {Name="Spotify",ExePath=@"C:\Apps\Spotify.exe"};var b=new AppRef {Name="ChatGPT",Aumid="OpenAI.ChatGPT!App"};var all=new[]{a,b};var usage=new SearchUsage();
        Assert.Empty(usage.Suggestions(all));usage.Record(a,DateTime.UtcNow);usage.Record(a,DateTime.UtcNow);usage.Record(b,DateTime.UtcNow);
        string root=Path.Combine(Path.GetTempPath(),"FloatingDock-search-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {var path=Path.Combine(root,"usage.json");JsonFile.Save(path,usage.Snapshot());var restored=SearchUsage.Load(path);Assert.Equal(new[]{a.Key,b.Key},restored.Suggestions(all).Select(app=>app.Key));Assert.Equal(2,restored.Snapshot()[a.Key].Count);}
        finally {Directory.Delete(root,true);}
    }
    [Fact] public void ExplicitUserApplicationIsKept()
    {Assert.True(PaletteAppPolicy.Include(new(){Name="Windows utility",ExePath=@"C:\Windows\System32\utility.exe",Source=AppSource.User}));}
}
