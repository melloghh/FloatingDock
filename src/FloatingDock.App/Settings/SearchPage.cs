using FloatingDock.Core.Configuration;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Settings;
internal sealed partial class SettingsWindow
{
    private UIElement SearchPage()
    {
        var s=S;
        var status=Ui.Note("");
        var hotkey=Ui.TextField("Open / close search",s.SearchHotkey,Ui.HotkeyError,(d,v)=>d.SearchHotkey=v,"Ctrl+Alt+K");
        hotkey.Children.Add(status);HookHotkeyStatus(hotkey,status,DockApp.HotkeySearch);
        return Page("Search",
            Ui.Section("Shortcut",hotkey),
            Ui.Section("Search apps",
                Ui.Slider("Maximum results",5,100,s.SearchResultLimit,(d,v)=>d.SearchResultLimit=v)),
            Ui.Action("Search",_app.ToggleSearch));
    }
}
