using FloatingDock.Core.Configuration;
using FloatingDock.Core.Geometry;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;
public sealed class InterfaceFeaturesTests
{
    [Fact] public void OldProfileGetsUsableEnglishDefaults()
    {
        using var d=new TempDir();File.WriteAllText(d.File("config.json"),"""{"SchemaVersion":2,"IconSize":37,"AutoHide":true}""");
        var settings=SettingsStore.Load(d.File("config.json"));
        Assert.Equal(37,settings.IconSize);Assert.True(settings.AutoHide);
        Assert.Equal(InterfaceLanguage.English,settings.Language);Assert.Equal(LaunchpadIconStyle.WhiteSquares,settings.LaunchpadIconStyle);
        Assert.Equal("Ctrl+Alt+K",settings.SearchHotkey);Assert.Equal("Desktop;Downloads",settings.SearchFolders);
    }
    [Fact] public void NewAnimationAndSearchValuesAreClamped()
    {
        var s=new DockSettings { Language=(InterfaceLanguage)99,LaunchpadIconStyle=(LaunchpadIconStyle)99,TopBarAnimationFps=900,TopBarHideMs=-1,TopBarShowMs=9000,TopBarHideDelayMs=0,PreviewDelayMs=20,MenuAnimationMs=9000,SearchResultLimit=10000 };
        s.Normalize();Assert.Equal(InterfaceLanguage.English,s.Language);Assert.Equal(LaunchpadIconStyle.WhiteSquares,s.LaunchpadIconStyle);
        Assert.Equal(144,s.TopBarAnimationFps);Assert.Equal(80,s.TopBarHideMs);Assert.Equal(800,s.TopBarShowMs);Assert.Equal(100,s.TopBarHideDelayMs);
        Assert.Equal(200,s.PreviewDelayMs);Assert.Equal(600,s.MenuAnimationMs);Assert.Equal(100,s.SearchResultLimit);
    }
    [Theory]
    [InlineData("Appearance","Оформление")][InlineData("Search","Поиск")][InlineData("Show system tray arrow","Стрелка системного трея")][InlineData("White squares","Белые квадраты")]
    public void LanguageRoundTrip(string english,string russian)
    { Assert.Equal(russian,InterfaceText.Get(english,InterfaceLanguage.Russian));Assert.Equal(english,InterfaceText.Get(russian,InterfaceLanguage.English)); }
    [Fact] public void LocalizationPreservesApplicationNamesAndPaths()
    { Assert.Equal("Adobe Photoshop 2021",InterfaceText.Get("Adobe Photoshop 2021",InterfaceLanguage.Russian));Assert.Equal(@"C:\Apps\Русское имя.exe",InterfaceText.Get(@"C:\Apps\Русское имя.exe",InterfaceLanguage.English)); }
    [Fact] public void DialogTitlesAllowCapitalizationVariants()=>Assert.Equal("Наборы иконок",InterfaceText.Get("Icon packs",InterfaceLanguage.Russian));
    [Fact] public void SideLabelHeadroomIsOutsideHoverKeepZone()
    {
        var parameters=GeometryParams.From(new DockSettings { Edge=DockEdge.Left,ShowLabels=true }) with { LabelHeadroom=260 };
        var metrics=WindowMetrics.Compute(parameters,12,1,1080);
        Assert.True(metrics.KeepZone.Y>250);Assert.False(metrics.KeepZone.Contains((metrics.RestPlateLeft+metrics.RestPlateRight)/2,100));
        Assert.True(metrics.KeepZone.Contains((metrics.RestPlateLeft+metrics.RestPlateRight)/2,metrics.PlateTop+20));
    }
}
