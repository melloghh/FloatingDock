using FloatingDock.Core.Configuration;
using FloatingDock.Core.Taskbar;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Taskbar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FloatingDock.UI;

public static class Program
{
    internal static AppPaths DataPaths { get; private set; } = AppPaths.Default();
    internal static bool IsSmokeTest { get; private set; }
    internal static bool IsDiagnosticProfile { get; private set; }
    internal static bool TestSystemTaskbar { get; private set; }
    internal static bool TestDefaultSettings { get; private set; }
    internal static int TestAnimationFps { get; private set; } = 60;
    internal static bool TestRunningTaskManager { get; private set; }
    internal static bool TestOnlyTaskManager { get; private set; }
    internal static int UiFailureCount { get; set; }
    /// <summary>
    /// <c>FloatingDock.exe --restore-taskbar [--quiet]</c>. Works whether or not the dock is running and never starts the UI.
    /// Exit code 0 = restored / nothing to restore, 1 = failed (record kept), 2 = the original state is unknown and a forced restore was not confirmed.
    /// <c>--quiet</c> (used by the uninstaller) shows no dialogs: an unknown state is reported by the exit code only and nothing is changed.
    /// </summary>
    private static int RestoreTaskbarCommand(bool quiet)
    {
        var paths = AppPaths.Default();
        Services.Logger? log = null;
        try { log = new Services.Logger(paths.LogsDir); } catch { /* logging is optional */ }
        var taskbar = new TaskbarController(paths.TaskbarStateFile, m => log?.Info(m));
        var r = TaskbarRestoreFlow.Run(taskbar, recoverOnly: false, interactive: !quiet);
        log?.Info($"--restore-taskbar: {r.Outcome} - {r.Message}");
        if (!quiet && r.Outcome != TaskbarOutcome.NothingToDo)
        {
            if (r.Success) NativeDialogs.Info(r.Message);
            else NativeDialogs.Error(r.Message);
        }
        return r.Success ? 0 : r.Outcome == TaskbarOutcome.NeedsConfirmation ? 2 : 1;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            int profile = Array.IndexOf(args, "--diagnostic-profile");
            if (profile >= 0)
            {
                if (profile + 1 >= args.Length || !Path.IsPathFullyQualified(args[profile + 1]))
                    throw new ArgumentException("--diagnostic-profile requires an absolute path.");
                var root = Path.GetFullPath(args[profile + 1]);
                if (root.TrimEnd(Path.DirectorySeparatorChar).Equals(AppPaths.Default().Root, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Use a separate copy of the user profile for diagnostics.");
                DataPaths = new AppPaths(root);
                IsDiagnosticProfile = true;
            }
            int smoke = Array.FindIndex(args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
            if (smoke >= 0)
            {
                IsSmokeTest = true;
                TestRunningTaskManager = args.Contains("--test-running-task-manager");
                TestOnlyTaskManager = args.Contains("--test-only-task-manager");
                int fpsIndex = Array.IndexOf(args, "--test-animation-fps");
                if (fpsIndex >= 0)
                {
                    if (fpsIndex + 1 >= args.Length || !int.TryParse(args[fpsIndex + 1], out var fps) || fps < 30 || fps > 144)
                        throw new ArgumentException("Test animation FPS must be 30..144.");
                    TestAnimationFps = fps;
                }
                if (smoke + 1 >= args.Length || !Path.IsPathFullyQualified(args[smoke + 1]))
                    throw new ArgumentException("--smoke-test requires an absolute path to a separate test-data directory.");
                var testRoot = Path.GetFullPath(args[smoke + 1]);
                if (Directory.Exists(testRoot) && Directory.EnumerateFileSystemEntries(testRoot).Any())
                    throw new ArgumentException("The smoke-test data directory must be empty.");
                DataPaths = new AppPaths(testRoot);
                TestSystemTaskbar = args.Any(a => a.Equals("--test-system-taskbar", StringComparison.OrdinalIgnoreCase));
                TestDefaultSettings = args.Any(a => a.Equals("--test-default-settings", StringComparison.OrdinalIgnoreCase));
                if (TestDefaultSettings && !TestSystemTaskbar)
                    throw new ArgumentException("--test-default-settings requires --test-system-taskbar.");
            }
            TraceStartup("entered Main");
            return Run(args);
        }
        catch (Exception ex)
        {
            ReportStartupError(ex);
            DockApp.EmergencyRestore(ex);
            return 1;
        }
    }

    internal static void ReportStartupError(Exception ex)
    {
        try { new Services.Logger(DataPaths.LogsDir).Error("FloatingDock could not start", ex); } catch { }
        if (!IsSmokeTest)
            NativeDialogs.Error("FloatingDock could not start. " + ex.Message + "\n\nDiagnostic log: " + Path.Combine(DataPaths.LogsDir, "floatingdock.log"));
    }

    internal static void TraceStartup(string stage)
    {
        try { new Services.Logger(DataPaths.LogsDir).Info("startup: " + stage); } catch { }
    }

    private static int Run(string[] args)
    {
        string instanceName = IsSmokeTest || IsDiagnosticProfile
            ? "FloatingDock.Diagnostic." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(DataPaths.Root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))
            : "FloatingDock";
        // Emergency exit that never starts the UI: puts the Windows taskbar back and quits.
        if (args.Any(a => a.Equals("--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
            return RestoreTaskbarCommand(args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)));

        // Used by the installer / uninstaller: ask the running dock to exit in an orderly way (it restores the taskbar first) and wait for it.
        if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
        {
            if (!SingleInstance.RequestQuit(instanceName)) return 0;                      // not running
            return SingleInstance.WaitUntilStopped(TimeSpan.FromSeconds(20), instanceName) ? 0 : 1;
        }

        // Used by "Restart": wait until the previous process is gone so the single-instance mutex is free.
        int waitIdx = Array.FindIndex(args, a => a.Equals("--wait-for-exit", StringComparison.OrdinalIgnoreCase));
        if (waitIdx >= 0 && waitIdx + 1 < args.Length && int.TryParse(args[waitIdx + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(10_000); } catch (ArgumentException) { }
        }

        using var single = new SingleInstance(instanceName);
        if (!single.IsFirst)
        {
            single.SignalFirstInstance();      // the running instance opens its settings
            return 0;
        }

        TraceStartup("initializing COM wrappers");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var systemCompositionQueue = new Services.SystemCompositionQueue();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DockApp.EmergencyRestore(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { DockApp.TryLog("unobserved task exception", e.Exception); e.SetObserved(); };

        TraceStartup("starting WinUI application");
        Application.Start(p =>
        {
            TraceStartup("WinUI dispatcher created");
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            var app = new DockApp(single, args);
            GC.KeepAlive(app);
        });
        systemCompositionQueue.KeepAlive();
        systemCompositionQueue.Shutdown();
        return Environment.ExitCode;
    }
}
