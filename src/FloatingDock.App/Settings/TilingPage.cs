using FloatingDock.Core.Configuration;
using FloatingDock.Core.Tiling;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;

namespace FloatingDock.UI.Settings;
internal sealed partial class SettingsWindow
{
    private UIElement TilingPage()
    {
        var s=S;
        var editorSettings=s.Clone();editorSettings.TilingWorkspaces=9;
        string Label(TilingShortcut binding)=>binding.Kind==TilingActionKind.Workspace?L.Format("Workspace {0}",binding.Workspace):binding.Kind==TilingActionKind.Send?L.Format("Send to workspace {0}",binding.Workspace):binding.Label;
        var bindings=TilingShortcuts.Definitions(editorSettings).Select(binding=>Ui.TextField(Label(binding),TilingShortcuts.Effective(s,binding),text=>
        {var candidate=S.Clone();candidate.TilingKeybindings[binding.Id]=text;return TilingShortcuts.Error(candidate);},(d,text)=>d.TilingKeybindings[binding.Id]=text)).ToArray();
        return Page("Tiling manager",
            Ui.Section("Window management",
                Ui.Toggle("Enable built-in tiling",s.TilingEnabled,(d,v)=>d.TilingEnabled=v),
                Ui.Toggle("Show workspace buttons on menu bar",s.TilingShowWorkspaces,(d,v)=>d.TilingShowWorkspaces=v),
                Ui.Toggle("Hide dock while tiled windows are visible",s.TilingHideDock,(d,v)=>d.TilingHideDock=v),
                Ui.Toggle("Bring activated apps into the current workspace",s.TilingBringActivatedWindows,(d,v)=>d.TilingBringActivatedWindows=v),
                Ui.Toggle("Tile apps that start maximized",s.TilingTileMaximizedApps,(d,v)=>d.TilingTileMaximizedApps=v),
                Ui.Note("Automatically arrange resizable apps. Dialogs and fullscreen apps float. Maximizing an already tiled window floats it. Turning this off restores positions."),
                Ui.Combo("Shortcut modifier",new[]{("Alt",TilingModifier.Alt),("Ctrl+Alt+Win",TilingModifier.CtrlAltWin)},s.TilingModifier,(d,v)=>{d.TilingModifier=v;_app.UiQueue.TryEnqueue(Refresh);}),
                Ui.Slider("Workspaces per monitor",1,9,s.TilingWorkspaces,(d,v)=>d.TilingWorkspaces=v)),
            Ui.Section("Layout & motion",
                Ui.Slider("Inner gap",0,40,s.TilingGap,(d,v)=>d.TilingGap=v),
                Ui.Slider("Outer gap",0,80,s.TilingOuterGap,(d,v)=>d.TilingOuterGap=v),
                Ui.Slider("Window animation duration",0,600,s.TilingAnimationMs,(d,v)=>d.TilingAnimationMs=v," ms"),
                Ui.Slider("Window animation FPS",30,144,s.TilingAnimationFps,(d,v)=>d.TilingAnimationFps=v),
                Ui.Slider("Wait for app startup",0,500,s.TilingStartupDelayMs,(d,v)=>d.TilingStartupDelayMs=v," ms"),
                Ui.Note("Wait briefly for startup sizing before tiling. Motion respects Windows animation preferences.")),
            Ui.Section("Window borders",
                Ui.Toggle("Add borders to tiled windows",s.TilingBorders,(d,v)=>d.TilingBorders=v),
                Ui.TextField("Focused window color",s.TilingActiveBorderColor,t=>ColorUtil.TryParseRgb(t,out _)?null:"Use a color like A8C7FA.",(d,v)=>d.TilingActiveBorderColor=v),
                Ui.TextField("Other windows color",s.TilingInactiveBorderColor,t=>ColorUtil.TryParseRgb(t,out _)?null:"Use a color like A8C7FA.",(d,v)=>d.TilingInactiveBorderColor=v),
                Ui.Note("Native borders require Windows 11. Disabling tiling releases the color override.")),
            Ui.Section("Rules",
                Ui.TextField("Excluded executable names",s.TilingExcludedApps,_=>null,(d,v)=>d.TilingExcludedApps=v),
                Ui.Note("Separate executable names with semicolons, for example game.exe;Photoshop.exe. FloatingDock windows are always excluded.")),
            Ui.Section("Keyboard shortcuts",
                Ui.Note("Edit any binding below. Leave it empty to disable that action. Duplicate shortcuts are rejected."),
                Ui.Note("Workspace bindings above your workspace count stay inactive."),
                Ui.Advanced(bindings),
                Ui.Action("Reset tiling shortcuts",()=>{_app.Settings.Update(d=>d.TilingKeybindings.Clear());Refresh();}),
                Ui.Note("Inactive workspaces are minimized, so their windows remain accessible through the dock even after an unexpected exit.")));
    }
}
