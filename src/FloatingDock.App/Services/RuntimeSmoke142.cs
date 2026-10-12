using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckRealApps142Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=new Dictionary<long,NativeTilingSession.Placement>();NativeTilingSession? session=null;
        async Task Wait(Func<bool> ready,string error)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<4000)await Task.Delay(20);Check(ready(),error);}
        try
        {
            var windows=app.Running.Snapshot().Where(w=>w.ExeFileName is { } exe&&(exe.Equals("zen.exe",StringComparison.OrdinalIgnoreCase)||exe.Equals("Spotify.exe",StringComparison.OrdinalIgnoreCase))).GroupBy(w=>w.ExeFileName,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToArray();
            Check(windows.Length==2,"Real Zen/Spotify windows are unavailable.");
            foreach(var window in windows)
            {
                var placement=new NativeTilingSession.Placement {Length=Marshal.SizeOf<NativeTilingSession.Placement>()};
                Check(GetFixturePlacement(new(window.Hwnd),ref placement),"Could not preserve real window placement.");saved[window.Hwnd]=placement;
                NativeMethods.ShowWindowAsync(new(window.Hwnd),NativeMethods.SW_RESTORE);
            }
            await Wait(()=>windows.All(w=>!NativeMethods.IsIconic(new(w.Hwnd))),"Real applications did not restore.");
            session=new NativeTilingSession {DiagnosticWindows=saved.Keys.ToHashSet()};
            session.Reconcile(windows.Select(w=>w with {IsMinimized=false}).ToArray(),0);
            var targets=session.Targets();Check(targets.Count==2,"Real applications did not receive two tiles.");
            foreach(var (id,r) in targets)Check(session.Move(id,r),"Real placement was rejected.");
            await Wait(()=>targets.All(p=>NativeMethods.GetWindowRect(new(p.Key),out var r)&&Math.Abs(r.Width-p.Value.Width)<=2&&Math.Abs(r.Height-p.Value.Height)<=2),"Real window sizes did not settle.");
            long zen=windows.First(w=>w.ExeFileName!.Equals("zen.exe",StringComparison.OrdinalIgnoreCase)).Hwnd;
            long player=windows.First(w=>w.ExeFileName!.Equals("Spotify.exe",StringComparison.OrdinalIgnoreCase)).Hwnd;
            Check(targets[zen].Width>=480&&targets[zen].Height>=320,"Real Zen collapsed into a sliver.");
            Check(targets[player].Width>=800&&targets[player].Height>=600,"Real Spotify minimum was violated.");
            checks["realZenAndSpotifyUsefulTiles"]=new{zen=targets[zen],spotify=targets[player]};
        }
        finally
        {
            session?.Dispose();
            foreach(var (id,preserved) in saved){var placement=preserved;placement.Flags|=4;SetFixturePlacement(new(id),ref placement);}
            await Wait(()=>saved.All(p=>{var placement=new NativeTilingSession.Placement {Length=Marshal.SizeOf<NativeTilingSession.Placement>()};return GetFixturePlacement(new(p.Key),ref placement)&&placement.Show==p.Value.Show&&placement.Normal.Left==p.Value.Normal.Left&&placement.Normal.Top==p.Value.Normal.Top&&placement.Normal.Right==p.Value.Normal.Right&&placement.Normal.Bottom==p.Value.Normal.Bottom;}),"Real application placement/state was not restored.");
            await Wait(()=>!app.Tiling.Animating,"Real application restore leaked an animation.");checks["realAppPlacementRestored"]=true;
        }
    }
}
