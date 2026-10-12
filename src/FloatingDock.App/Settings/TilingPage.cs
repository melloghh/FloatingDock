using FloatingDock.Core.Configuration;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;

namespace FloatingDock.UI.Settings;
internal sealed partial class SettingsWindow
{
    private UIElement TilingPage()
    {
        var s=S;string modifier=s.TilingModifier==TilingModifier.Alt?"Alt":"Ctrl+Alt+Win";
        return Page("Tiling manager",
            Ui.Section("Window management",
                Ui.Toggle("Enable built-in tiling",s.TilingEnabled,(d,v)=>d.TilingEnabled=v),
                Ui.Toggle("Show workspace buttons on menu bar",s.TilingShowWorkspaces,(d,v)=>d.TilingShowWorkspaces=v),
                Ui.Note("Automatically arrange resizable windows. Dialogs, maximized windows and excluded apps float. Turning this off restores window positions."),
                Ui.Combo("Shortcut modifier",new[]{("Alt",TilingModifier.Alt),("Ctrl+Alt+Win",TilingModifier.CtrlAltWin)},s.TilingModifier,(d,v)=>d.TilingModifier=v),
                Ui.Slider("Workspaces per monitor",1,9,s.TilingWorkspaces,(d,v)=>d.TilingWorkspaces=v)),
            Ui.Section("Layout & motion",
                Ui.Slider("Inner gap",0,40,s.TilingGap,(d,v)=>d.TilingGap=v),
                Ui.Slider("Outer gap",0,80,s.TilingOuterGap,(d,v)=>d.TilingOuterGap=v),
                Ui.Slider("Window animation duration",0,400,s.TilingAnimationMs,(d,v)=>d.TilingAnimationMs=v)),
            Ui.Section("Rules",
                Ui.TextField("Excluded executable names",s.TilingExcludedApps,_=>null,(d,v)=>d.TilingExcludedApps=v),
                Ui.Note("Separate executable names with semicolons, for example game.exe;Photoshop.exe. FloatingDock windows are always excluded.")),
            Ui.Section("Keyboard shortcuts",
                Ui.Note(L.Format("{0} + H / J / K / L → focus left / down / up / right\n{0} + Shift + H / J / K / L → move\n{1} + H / J / K / L → resize\n{0} + 1…9 → workspace\n{0} + Shift + 1…9 → send to workspace\n{0} + F → floating\n{0} + V → split direction\n{0} + P → pause / resume",modifier,"Ctrl+Alt+Shift")),
                Ui.Note("Inactive workspaces are minimized, so their windows remain accessible through the dock even after an unexpected exit.")));
    }
}
