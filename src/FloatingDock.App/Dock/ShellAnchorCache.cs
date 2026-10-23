using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.UI.Dock;

/// <summary>Immutable UI-published geometry; the shell thread never reads XAML or live dock motion.</summary>
internal sealed class ShellAnchorCache
{
    internal readonly record struct Anchor(AppRef App,IReadOnlyList<WindowInfo> Windows,RECT Rect);
    private sealed record Dock(IntPtr Monitor,RECT Fallback,Anchor[] Anchors);
    private readonly Dictionary<IntPtr,Dock> _docks=new(); // UI thread only
    private Dock[] _snapshot=Array.Empty<Dock>();
    internal void Update(IntPtr window,IntPtr monitor,RECT fallback,Anchor[] anchors)
    {
        _docks[window]=new(monitor,fallback,anchors);
        Volatile.Write(ref _snapshot,_docks.Values.ToArray());
    }
    internal void Remove(IntPtr window){_docks.Remove(window);Volatile.Write(ref _snapshot,_docks.Values.ToArray());}
    internal RECT? Get(IntPtr window)
    {
        var docks=Volatile.Read(ref _snapshot);if(docks.Length==0)return null;
        var root=NativeMethods.GetAncestor(window,NativeMethods.GA_ROOTOWNER);if(root!=IntPtr.Zero)window=root;
        if(!NativeMethods.IsWindow(window))return null;
        NativeMethods.GetWindowThreadProcessId(window,out var pid);
        var monitor=NativeMethods.MonitorFromWindow(window,NativeMethods.MONITOR_DEFAULTTONEAREST);
        // Exact HWND/PID wins, with the nearest displayed dock preferred on multiple monitors.
        foreach(bool nearest in new[]{true,false})foreach(var dock in docks)
            if((dock.Monitor==monitor)==nearest)foreach(var anchor in dock.Anchors)
                if(anchor.Windows.Any(w=>w.Hwnd==window.ToInt64()&&(w.ProcessId==pid||WindowActivator.IsLive(w))))return anchor.Rect;
        // New windows can reach the shell before the asynchronous running-app scan.
        // Match an already displayed app icon by native identity without waiting for that scan.
        string? exe=WindowIdentityResolver.GetExePath(pid);
        string? aumid=AppMatching.IsHostExe(exe)?WindowIdentityResolver.GetProcessAumid(pid):null;
        var info=new WindowInfo(window.ToInt64(),(int)pid,exe,aumid,null,null,NativeMethods.IsIconic(window));
        foreach(bool nearest in new[]{true,false})foreach(var dock in docks)
            if((dock.Monitor==monitor)==nearest)foreach(var anchor in dock.Anchors)
                if(AppMatching.Matches(anchor.App,info))return anchor.Rect;
        // Process queries can fail for elevated/protected or exiting owners.
        // A valid shell request still needs the dock fallback in that case.
        if(pid==(uint)Environment.ProcessId||exe is not null&&WindowFilter.IsIgnoredExe(exe))return null;
        // An unpinned icon may still be entering. Keep even that first minimize on the dock.
        return (docks.FirstOrDefault(d=>d.Monitor==monitor)??docks[0]).Fallback;
    }
}
