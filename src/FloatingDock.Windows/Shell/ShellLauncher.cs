using System.ComponentModel;
using System.Diagnostics;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Shell;

/// <summary>Launches applications through ShellExecute: shortcuts, Store apps (AppsFolder), plain executables. Never creates a window of its own.</summary>
public static class ShellLauncher
{
    public static bool Launch(AppRef app, bool runAsNewInstance = false)
    {
        try
        {
            if (!string.IsNullOrEmpty(app.ShortcutPath) && File.Exists(app.ShortcutPath)) return Execute(app.ShortcutPath, null, null);
            if (!string.IsNullOrEmpty(app.Aumid) && (app.Aumid.Contains('!') || string.IsNullOrEmpty(app.ExePath)))
                return Execute("shell:AppsFolder\\" + app.Aumid, null, null);
            if (!string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath))
                return Execute(app.ExePath, app.Arguments, app.WorkingDirectory ?? Path.GetDirectoryName(app.ExePath));
            if (!string.IsNullOrEmpty(app.ShellParsingName)) return Execute("shell:AppsFolder\\" + app.ShellParsingName, null, null);
        }
        catch (Win32Exception) { }
        return false;
    }

    public static bool Execute(string file, string? args, string? workingDir)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_FLAG_NO_UI,
            lpFile = file,
            lpParameters = string.IsNullOrWhiteSpace(args) ? null : args,
            lpDirectory = string.IsNullOrWhiteSpace(workingDir) ? null : workingDir,
            nShow = NativeMethods.SW_SHOWNORMAL,
        };
        return NativeMethods.ShellExecuteEx(ref info);
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static bool RevealInExplorer(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return true;
        }
        catch (Win32Exception) { return false; }
    }

    public static string? LocationOf(AppRef app)
    {
        if (!string.IsNullOrEmpty(app.ShortcutPath) && File.Exists(app.ShortcutPath)) return app.ShortcutPath;
        if (!string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath)) return app.ExePath;
        return null;
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false }); } catch (Win32Exception) { }
    }
}
