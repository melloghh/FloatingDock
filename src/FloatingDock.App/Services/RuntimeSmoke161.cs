using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.UI.Dock;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using Microsoft.UI.Xaml;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static async Task CheckAnimation161Async(DockApp app,Dictionary<string,object> checks)
    {
        var probe=new Window{Title="FloatingDock hidden shell destination regression"};
        var hwnd=WindowSetup.Hwnd(probe);
        try
        {
            var cache=new ShellAnchorCache();
            var rect=new RECT{Left=123,Top=456,Right=163,Bottom=496};
            var appRef=new AppRef{Name="Owned hidden fixture"};
            var info=new WindowInfo(hwnd.ToInt64(),Environment.ProcessId,null,null,null,null,false);
            cache.Update(new IntPtr(1),IntPtr.Zero,rect,new[]{new ShellAnchorCache.Anchor(appRef,new[]{info},rect)});
            Check(!NativeMethods.IsWindowVisible(hwnd),"Fixture must be hidden during the shell request.");
            var result=cache.Get(hwnd);
            Check(result is { } r&&r.Left==123&&r.Top==456,"A hidden transition frame lost its dock destination.");
            checks["hiddenShellDestination"]=true;
        }
        finally {probe.Close();}
        var saved=app.Settings.Current.Edge;
        try
        {
            foreach(var edge in new[]{DockEdge.Bottom,DockEdge.Left,DockEdge.Right})
            {
                app.Settings.Update(s=>s.Edge=edge);
                await Task.Delay(180);
                await CheckUi143Async(app,checks);
                checks["minimizeEdge"+edge]=true;
            }
        }
        finally {app.Settings.Update(s=>s.Edge=saved);}
    }
}
