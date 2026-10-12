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
            Ui.Section("Search apps, files and folders",
                Ui.Toggle("Search files and folders",s.SearchFiles,(d,v)=>d.SearchFiles=v),
                Ui.TextField("Search folders (separated by semicolons)",s.SearchFolders,FloatingDock.Windows.Shell.FileSearch.ValidateFolders,(d,v)=>d.SearchFolders=v),
                Ui.Note("Use Desktop;Downloads or absolute folder paths. Documents is excluded. Only personal file types and top-level folders are shown."),
                Ui.Slider("Subfolder depth",0,3,s.SearchDepth,(d,v)=>d.SearchDepth=v),
                Ui.Note("Folder lists are cached for 30 seconds. File contents are not searched."),
                Ui.Slider("Maximum results",5,100,s.SearchResultLimit,(d,v)=>d.SearchResultLimit=v)),
            Ui.Action("Refresh file index",FloatingDock.Windows.Shell.FileSearch.Invalidate),
            Ui.Action("Search",_app.ToggleSearch));
    }
}
