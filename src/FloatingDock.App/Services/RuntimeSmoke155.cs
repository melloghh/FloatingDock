using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static async Task CheckCpu155Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;var dock=app.Dock.PrimaryWindowForDiagnostics!;
        app.Dock.HoldFullscreenStateForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;dock.IgnoreRealCursorForDiagnostics=true;
        try
        {
            app.Settings.Update(s=>{s.TopBarEnabled=true;s.TopBarAutoHide=false;s.Language=InterfaceLanguage.English;s.MenuAnimations=true;});
            bar.SetFullscreen(false);bar.Target(false);await Task.Delay(250);
            int start=bar.Menus.MainContentBuildsForDiagnostics;
            for(int i=0;i<4;i++)
            {
                bar.OpenMenuForDiagnostics("main");await Task.Delay(360);
                Check(bar.Menus.ActiveKind=="main"&&bar.Menus.SurfaceVisible,"Reused menu did not reopen.");
                Check(bar.Menus.MotionForDiagnostics!.VisualMatchesProgressForDiagnostics,"Reused menu retained a stale transform.");
                Check(bar.Menus.MainContentBuildsForDiagnostics==start+1,"Unchanged main menu rebuilt its controls.");
                bar.Menus.Close();await Task.Delay(230);
                Check(!bar.Menus.SurfaceVisible&&!NativeMethods.IsWindowVisible(bar.Menus.PopupHandle),"Cached controls left a visible surface.");
            }
            bar.OpenMenuForDiagnostics("main");await Task.Delay(360);
            var scroll=bar.Menus.ScrollForDiagnostics!;var panel=(StackPanel)scroll.Content;
            var spacer=new Border{Height=1800};panel.Children.Add(spacer);
            try
            {
                scroll.UpdateLayout();scroll.ChangeView(null,200,null,true);await Task.Delay(100);
                Check(scroll.VerticalOffset>0,"Scroll reuse fixture did not scroll.");
                bar.Menus.Close(immediately:true);bar.OpenMenuForDiagnostics("main");await Task.Delay(360);
                Check(scroll.VerticalOffset==0,"Reused menu kept the previous scroll position.");
            }
            finally {panel.Children.Remove(spacer);bar.Menus.Close(immediately:true);}
            app.Settings.Update(s=>s.Language=InterfaceLanguage.Russian);await Task.Delay(150);
            bar.OpenMenuForDiagnostics("main");await Task.Delay(360);
            Check(bar.Menus.MainContentBuildsForDiagnostics==start+2,"Language change did not invalidate menu content.");
            OwnWindowCapture.Capture(bar.Menus.PopupHandle).Save(Path.Combine(app.Paths.Root,"cached-menu-russian.png"));
            bar.Menus.Close(immediately:true);
            app.Settings.Update(s=>s.TopBarAutoHide=true);await Task.Delay(150);bar.Target(false);
            bar.OpenMenuForDiagnostics("main");await Task.Delay(360);
            Check(bar.Menus.MainContentBuildsForDiagnostics==start+3,"Auto-hide change did not invalidate the selected row.");
            bar.Menus.Close(immediately:true);
            checks["mainMenuReuse"]=new{reopenCycles=5,scrollReset=true,languageInvalidation=true,selectionInvalidation=true};
        }
        finally
        {
            bar.Menus.Close(immediately:true);bar.IgnoreRealCursorForDiagnostics=false;dock.IgnoreRealCursorForDiagnostics=false;
            app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
}
