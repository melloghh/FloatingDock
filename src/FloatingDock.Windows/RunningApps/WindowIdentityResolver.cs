using System.Text;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Shell;

namespace FloatingDock.Windows.RunningApps;

/// <summary>Maps an HWND to its process, executable path and AppUserModelID - including windows hosted by ApplicationFrameHost (Settings, UWP).</summary>
public static class WindowIdentityResolver
{
    // Early tiling needs only native ownership/exclusions, without waiting for Shell/property-store work.
    public static WindowInfo ResolveBasic(IntPtr hwnd)
    {NativeMethods.GetWindowThreadProcessId(hwnd,out var pid);return new(hwnd.ToInt64(),(int)pid,GetExePath(pid),null,NativeMethods.GetTitle(hwnd),NativeMethods.GetClass(hwnd),NativeMethods.IsIconic(hwnd));}
    public sealed record Result(WindowInfo Info, bool Complete);

    /// <summary>Must run on an STA thread (property store).</summary>
    public static Result Resolve(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        var cls = NativeMethods.GetClass(hwnd);
        var title = NativeMethods.GetTitle(hwnd);
        var exe = GetExePath(pid);
        var aumid = ShellHelpers.ReadAumidOfWindow(hwnd);
        if (string.IsNullOrWhiteSpace(aumid)) aumid = null;
        bool isFrame = AppMatching.IsHostExe(exe) || string.Equals(cls, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);

        if (isFrame)
        {
            // the real application lives in a child CoreWindow that belongs to another process (ApplicationFrameHost only draws the frame)
            var (childPid, childExe, childAumid) = FindHostedApp(hwnd, pid);
            if (childPid != 0)
            {
                pid = childPid;
                if (childExe is not null) exe = childExe;                // the hosted app's executable replaces the host's
                aumid ??= childAumid;
            }
        }
        else if (aumid is null)
        {
            // packaged desktop apps expose their AUMID through the process even if the window has none
            var processAumid = GetProcessAumid(pid);
            if (processAumid is not null && processAumid.Contains('!')) aumid = processAumid;
        }
        // complete = a real executable, or (for hosted windows) an AUMID. A child PID without either is NOT an identity.
        bool complete = AppMatching.IsIdentityComplete(exe, aumid, isFrame);
        return new Result(new WindowInfo(hwnd.ToInt64(), (int)pid, exe, aumid, title, cls, NativeMethods.IsIconic(hwnd)), complete);
    }

    private static (uint Pid, string? Exe, string? Aumid) FindHostedApp(IntPtr frame, uint framePid)
    {
        uint foundPid = 0;
        NativeMethods.EnumChildWindows(frame, (child, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(child, out var cpid);
            if (cpid != 0 && cpid != framePid && foundPid == 0 && NativeMethods.GetClass(child) == "Windows.UI.Core.CoreWindow") { foundPid = cpid; return false; }
            return true;
        }, IntPtr.Zero);
        if (foundPid == 0) return (0, null, null);
        return (foundPid, GetExePath(foundPid), GetProcessAumid(foundPid));
    }

    public static string? GetExePath(uint pid)
    {
        if (pid == 0) return null;
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    public static string? GetExePath(int pid) => GetExePath((uint)pid);

    public static string? GetProcessAumid(uint pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            int rc = NativeMethods.GetApplicationUserModelId(h, ref len, null);          // ERROR_INSUFFICIENT_BUFFER (122) when the process is packaged
            if (rc != 122 || len == 0) return null;
            var sb = new StringBuilder((int)len);
            return NativeMethods.GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }
}
