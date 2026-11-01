using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Taskbar;
using FloatingDock.Ipc;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Taskbar;

namespace FloatingDock.Engine;

/// <summary>
/// The FloatingDock program: <c>FloatingDock.Engine.exe</c> runs the engine and, next to it, the Flutter UI. Started a second time it asks the
/// running one to open the settings. Other forms:
/// <list type="bullet">
/// <item><c>--quit</c> stops the running engine and waits for it;</item>
/// <item><c>--restore-taskbar [--quiet]</c> puts the Windows taskbar back without starting anything (exit 0 restored / nothing to do, 1 failed, 2 state unknown);</item>
/// <item><c>--settings</c> opens the settings window at start; <c>--enable-autostart</c> turns on "Start with Windows" (the installer);</item>
/// <item><c>--ui &lt;exe&gt;</c> uses another UI; <c>--no-ui</c> runs the engine alone; <c>--classic &lt;exe&gt;</c> where the WinUI application is;</item>
/// <item><c>--diagnostic-profile &lt;absolute dir&gt;</c> works on a copy of the profile and never touches the desktop (no tray, hotkeys or taskbar changes).</item>
/// </list>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var paths = AppPaths.Default();
            int profile = Array.IndexOf(args, "--diagnostic-profile");
            if (profile >= 0)
            {
                if (profile + 1 >= args.Length || !Path.IsPathFullyQualified(args[profile + 1]))
                    throw new ArgumentException("--diagnostic-profile requires an absolute path.");
                var root = Path.GetFullPath(args[profile + 1]).TrimEnd(Path.DirectorySeparatorChar);
                if (root.Equals(AppPaths.Default().Root, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Use a separate copy of the user profile for diagnostics.");
                paths = new AppPaths(root);
            }
            if (args.Contains("--restore-taskbar")) return RestoreTaskbar(paths, args.Contains("--quiet"));
            if (args.Contains("--quit")) return Quit(paths);
            return Run(paths, args, profile >= 0);
        }
        catch (Exception ex)
        {
            EngineLog.Error("engine could not start", ex);
            return 1;
        }
    }

    private static int Run(AppPaths paths, string[] args, bool diagnostic)
    {
        // Used by "Restart": wait until the previous process is gone so the single-instance mutex is free.
        int wait = Array.IndexOf(args, "--wait-for-exit");
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(10_000); } catch (ArgumentException) { }
        }
        EngineLog.Open(paths.LogsDir);

        // The interface is a setting: "Classic" hands over to the WinUI application, which reads the same profile.
        if (!diagnostic && SettingsStore.Load(paths.ConfigFile).UiMode == "Classic" && StartClassic(args))
        {
            EngineLog.Info("UiMode is Classic: started the WinUI application and left");
            return 0;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..16];
        using var single = new Mutex(initiallyOwned: true, @"Local\FloatingDock.Engine." + hash, out bool first);
        if (!first)
        {
            // Started again: the running one opens its settings (that is what a second launch has always meant).
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var client = IpcClient.ConnectAsync(IpcEndpoint.SocketPathFor(paths.Root), "second-launch", cts.Token).GetAwaiter().GetResult();
                client.RequestAsync("app.activate", timeout: TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) { EngineLog.Info("another engine already serves this profile (" + ex.Message + ")"); }
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => EngineLog.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { EngineLog.Error("unobserved task exception", e.Exception); e.SetObserved(); };

        var ui = new UiLoop();
        using var host = new EngineHost(paths, ui, args, diagnostic);
        UiSupervisor? supervisor = null;
        var uiExe = args.Contains("--no-ui") ? null : UiSupervisor.Find(args);
        if (uiExe is not null) supervisor = new UiSupervisor(uiExe, IpcEndpoint.SocketPathFor(paths.Root), args.Contains("--settings"));
        else if (!diagnostic && !args.Contains("--no-ui")) EngineLog.Warn("no UI found next to the engine (ui\\fd_ui.exe); running without one");
        host.QuitRequested += () => { supervisor?.Stop(); ui.Quit(); };
        ui.Post(() =>
        {
            host.Start();
            supervisor?.Start();
        });
        ui.Run();
        supervisor?.Dispose();
        return 0;
    }

    /// <summary>The WinUI application: <c>classic\FloatingDock.exe</c> next to the engine, or the path given with <c>--classic</c>.</summary>
    private static bool StartClassic(string[] args)
    {
        int i = Array.IndexOf(args, "--classic");
        var exe = i >= 0 && i + 1 < args.Length ? args[i + 1] : Path.Combine(AppContext.BaseDirectory, "classic", "FloatingDock.exe");
        if (!File.Exists(exe)) { EngineLog.Warn("UiMode is Classic but " + exe + " does not exist; staying with the new interface"); return false; }
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args.Where(a => a is "--settings" or "--enable-autostart")) info.ArgumentList.Add(a);
            Process.Start(info);
            return true;
        }
        catch (Exception ex) { EngineLog.Error("the WinUI application could not be started", ex); return false; }
    }

    private static int RestoreTaskbar(AppPaths paths, bool quiet)
    {
        EngineLog.Open(paths.LogsDir);
        var taskbar = new TaskbarController(paths.TaskbarStateFile, m => EngineLog.Info(m));
        var r = TaskbarRestoreFlow.Run(taskbar, recoverOnly: false, interactive: !quiet);
        EngineLog.Info($"--restore-taskbar: {r.Outcome} - {r.Message}");
        if (!quiet && r.Outcome != TaskbarOutcome.NothingToDo)
        {
            if (r.Success) NativeDialogs.Info(r.Message);
            else NativeDialogs.Error(r.Message);
        }
        return r.Success ? 0 : r.Outcome == TaskbarOutcome.NeedsConfirmation ? 2 : 1;
    }

    private static int Quit(AppPaths paths)
    {
        var socket = IpcEndpoint.SocketPathFor(paths.Root);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var client = IpcClient.ConnectAsync(socket, "engine-quit", cts.Token).GetAwaiter().GetResult();
            client.RequestAsync(Protocol.Methods.EngineQuit, timeout: TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception) { return 0; }                              // nothing is listening: not running
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(socket)) return 0;
            Thread.Sleep(100);
        }
        return 1;
    }
}
