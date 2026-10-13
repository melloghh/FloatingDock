namespace FloatingDock.Core.Models;

/// <summary>Platform-neutral snapshot of a taskbar-eligible top-level window.</summary>
public sealed record WindowInfo(
    long Hwnd,
    int ProcessId,
    string? ExePath,
    string? Aumid,
    string? Title,
    string? ClassName,
    bool IsMinimized)
{
    public string? ExeFileName => string.IsNullOrEmpty(ExePath) ? null : WinPath.FileName(ExePath);
}

public static class AppMatching
{
    private static readonly HashSet<string> HostExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe", "wwahost.exe",
    };

    public const string ExplorerAumid = "Microsoft.Windows.Explorer";

    public static bool IsExplorer(AppRef app) => !app.IsSeparator &&
        (string.Equals(app.Aumid, ExplorerAumid, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(app.ShellParsingName, ExplorerAumid, StringComparison.OrdinalIgnoreCase) ||
         (!string.IsNullOrEmpty(app.ExePath) &&
          string.Equals(WinPath.FileName(app.ExePath), "explorer.exe", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Is the identity of a window final? A host process (ApplicationFrameHost / WWAHost) is never an identity by itself: the real app is the hosted one.
    /// Finding only the PID of a hosted app does not count - without its executable or an AUMID the window still has to be looked at again.
    /// </summary>
    public static bool IsIdentityComplete(string? exePath, string? aumid, bool isFrameHost)
    {
        bool hasRealExe = !string.IsNullOrEmpty(exePath) && !IsHostExe(exePath);
        if (!isFrameHost) return hasRealExe || IsPackagedAumid(aumid);
        return hasRealExe || !string.IsNullOrEmpty(aumid);
    }

    public static bool IsHostExe(string? exePath) =>
        !string.IsNullOrEmpty(exePath) && HostExes.Contains(WinPath.FileName(exePath));

    public static string NormalizePath(string path) =>
        path.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    // Steam's client UI runs in a CEF helper. Only helpers inside that client's bin/cef tree belong to it.
    public static string CanonicalExePath(string path)
    {
        var normalized = NormalizePath(path);
        int cef = normalized.IndexOf("\\bin\\cef\\", StringComparison.Ordinal);
        return cef > 0 && WinPath.FileName(normalized) == "steamwebhelper.exe"
            ? normalized[..cef] + "\\steam.exe" : normalized;
    }

    /// <summary>True if the window belongs to the app (exe path, AUMID, or Explorer special case).</summary>
    public static bool Matches(AppRef app, WindowInfo w)
    {
        if (app.IsSeparator) return false;
        if (!string.IsNullOrEmpty(app.Aumid) && !string.IsNullOrEmpty(w.Aumid) &&
            string.Equals(app.Aumid, w.Aumid, StringComparison.OrdinalIgnoreCase)) return true;

        if (!string.IsNullOrEmpty(app.ExePath) && !string.IsNullOrEmpty(w.ExePath) && !IsHostExe(w.ExePath) &&
            string.Equals(CanonicalExePath(app.ExePath), CanonicalExePath(w.ExePath), StringComparison.Ordinal))
        {
            // two different packaged apps (both AUMIDs are "family!app") that share an executable are not the same app
            if (IsPackagedAumid(app.Aumid) && IsPackagedAumid(w.Aumid) && !string.Equals(app.Aumid, w.Aumid, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // File Explorer pinned by AppID only: its windows may report no AUMID.
        if (string.Equals(app.Aumid, ExplorerAumid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(w.ExeFileName, "explorer.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsPackagedAumid(string? aumid) => !string.IsNullOrEmpty(aumid) && aumid.Contains('!');

    /// <summary>True when both apps are the same application: the same Key, or the same (packaged) AUMID.</summary>
    public static bool AreEquivalent(AppRef a, AppRef b) =>
        a.Key == b.Key || (IsPackagedAumid(a.Aumid) && string.Equals(a.Aumid, b.Aumid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Group key of a window that matches no pinned app; windows of the same program share it.</summary>
    public static string GroupKey(WindowInfo w)
    {
        if (!string.IsNullOrEmpty(w.Aumid) && w.Aumid.Contains('!')) return "aumid:" + w.Aumid.ToLowerInvariant();
        if (!string.IsNullOrEmpty(w.ExePath) && !IsHostExe(w.ExePath)) return "exe:" + CanonicalExePath(w.ExePath);
        if (!string.IsNullOrEmpty(w.Aumid)) return "aumid:" + w.Aumid.ToLowerInvariant();
        if (!string.IsNullOrEmpty(w.ExePath)) return "win:" + w.Hwnd;      // a host process whose real app is unknown (yet): keep it, never drop it
        return w.ProcessId > 0 ? "pid:" + w.ProcessId : "win:" + w.Hwnd;
    }

    /// <summary>Creates the app identity for an unpinned window group (used when pinning a running app).</summary>
    public static AppRef FromWindow(WindowInfo w, string? displayName = null)
    {
        var app = new AppRef
        {
            Name = displayName ?? DefaultName(w),
            ExePath = IsHostExe(w.ExePath) ? null : w.ExePath is null ? null : CanonicalExePath(w.ExePath),
            Aumid = w.Aumid,
            Source = AppSource.Window,
        };
        // A normal Win32 app is identified by its exe; keep the AUMID only when it is the only identity (packaged apps).
        if (!string.IsNullOrEmpty(app.ExePath) && !string.IsNullOrEmpty(app.Aumid) && !app.Aumid.Contains('!')) app.Aumid = null;
        return app;
    }

    public static string DefaultName(WindowInfo w)
    {
        if (!string.IsNullOrEmpty(w.ExePath) && !IsHostExe(w.ExePath)) return WinPath.FileNameWithoutExtension(CanonicalExePath(w.ExePath));
        if (!string.IsNullOrEmpty(w.Aumid))
        {
            var pkg = w.Aumid.Split('!')[0].Split('_')[0];
            var dot = pkg.LastIndexOf('.');
            return dot >= 0 ? pkg[(dot + 1)..] : pkg;
        }
        return string.IsNullOrWhiteSpace(w.Title) ? "App" : w.Title!;
    }
}
