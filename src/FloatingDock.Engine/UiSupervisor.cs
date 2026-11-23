using System.Diagnostics;

namespace FloatingDock.Engine;

/// <summary>
/// Starts the Flutter UI process and keeps it alive: if it crashes it is started again (not more than a few times a minute,
/// so a UI that cannot start does not spin the machine). The engine never depends on it: without a UI it is just quiet.
/// </summary>
internal sealed class UiSupervisor : IDisposable
{
    private readonly string _exe;
    private readonly Func<string> _socketPath;
    private readonly bool _openSettings;
    private readonly List<DateTime> _starts = new();
    private Process? _process;
    private bool _stopping;

    public UiSupervisor(string exe, Func<string> socketPath, bool openSettings)
    {
        _exe = exe;
        _socketPath = socketPath;
        _openSettings = openSettings;
    }

    /// <summary>The UI next to the engine: <c>ui\fd_ui.exe</c>, or the path given with <c>--ui</c>. Null when there is none.</summary>
    public static string? Find(string[] args)
    {
        int i = Array.IndexOf(args, "--ui");
        if (i >= 0 && i + 1 < args.Length) return File.Exists(args[i + 1]) ? Path.GetFullPath(args[i + 1]) : null;
        var local = Path.Combine(AppContext.BaseDirectory, "ui", "fd_ui.exe");
        return File.Exists(local) ? local : null;
    }

    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    public void Start()
    {
        if (_stopping) return;
        bool first;
        lock (_starts)
        {
            _starts.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(1));
            if (_starts.Count >= 4) { EngineLog.Error("the UI keeps stopping; it will not be started again until the engine restarts"); return; }
            first = _starts.Count == 0;
            _starts.Add(DateTime.UtcNow);
        }
        try
        {
            var info = new ProcessStartInfo(_exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_exe)! };
            info.ArgumentList.Add("--socket");
            info.ArgumentList.Add(_socketPath());
            if (_openSettings && first) info.ArgumentList.Add("--open-settings");
            _process = Process.Start(info);
            if (_process is null) return;
            _process.EnableRaisingEvents = true;
            var started = _process;
            started.Exited += (_, _) =>
            {
                if (_stopping) return;
                EngineLog.Warn($"the UI stopped (exit code {SafeExit(started)}); starting it again");
                Task.Delay(500).ContinueWith(_ => Start());
            };
            EngineLog.Info($"UI started: {_exe} (pid {started.Id})");
        }
        catch (Exception ex) { EngineLog.Error("the UI could not be started", ex); }
    }

    private static string SafeExit(Process p) { try { return p.ExitCode.ToString(); } catch (Exception) { return "?"; } }

    /// <summary>Gives the UI a moment to leave by itself (it does, on engine.stopping), then ends it.</summary>
    public void Stop()
    {
        _stopping = true;
        var p = _process;
        if (p is null) return;
        try
        {
            if (!p.HasExited && !p.WaitForExit(2500)) p.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* already gone */ }
    }

    public void Dispose() => Stop();
}
