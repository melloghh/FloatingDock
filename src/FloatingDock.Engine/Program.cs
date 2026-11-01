using System.Security.Cryptography;
using System.Text;
using FloatingDock.Core.Configuration;
using FloatingDock.Ipc;

namespace FloatingDock.Engine;

/// <summary>
/// <c>FloatingDock.Engine.exe [--diagnostic-profile &lt;absolute dir&gt;]</c> runs the engine;
/// <c>FloatingDock.Engine.exe --quit [--diagnostic-profile ...]</c> asks the running one to stop and waits for it.
/// A diagnostic profile never touches the user's real data and has its own single-instance lock and socket.
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
            return args.Contains("--quit") ? Quit(paths) : Run(paths);
        }
        catch (Exception ex)
        {
            EngineLog.Error("engine could not start", ex);
            return 1;
        }
    }

    private static int Run(AppPaths paths)
    {
        EngineLog.Open(paths.LogsDir);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..16];
        using var single = new Mutex(initiallyOwned: true, @"Local\FloatingDock.Engine." + hash, out bool first);
        if (!first)
        {
            EngineLog.Info("another engine already serves this profile");
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => EngineLog.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { EngineLog.Error("unobserved task exception", e.Exception); e.SetObserved(); };

        var ui = new UiLoop();
        using var host = new EngineHost(paths, ui);
        ui.Post(host.Start);
        ui.Run();
        return 0;
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
