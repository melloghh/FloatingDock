using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Ipc;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Engine;

/// <summary>What the top bar shows that Windows tells us about: the foreground app's name, keyboard layout, battery, network, fullscreen windows.</summary>
internal sealed partial class EngineHost
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte Ac, Flags, Percent, Reserved; public uint Life, FullLife; }

    private System.Threading.Timer? _languageTimer, _fullscreenTimer;
    private string _languageTag = "";
    private IntPtr _languageSource;
    private string _fullscreenState = "";

    private void RegisterBarHandlers(IpcServer server)
    {
        server.RegisterSync("battery.get", _ =>
        {
            if (GetSystemPowerStatus(out var p) && p.Percent <= 100 && (p.Flags & 128) == 0)
                return new JsonObject { ["present"] = true, ["percent"] = p.Percent, ["charging"] = p.Ac == 1 };
            return new JsonObject { ["present"] = false };
        });
        server.RegisterSync("network.available", _ => NetworkInterface.GetIsNetworkAvailable());
        server.RegisterSync("language.current", _ => new JsonObject { ["tag"] = CurrentLanguageTag() ?? _languageTag });
        server.RegisterSync("fullscreen.get", _ => FullscreenNode());
        server.RegisterSync("monitors.get", _ => MonitorsNode());

        NetworkChange.NetworkAvailabilityChanged += (_, e) => _server.Publish("network.changed", new JsonObject { ["available"] = e.IsAvailable });

        // The keyboard layout is a property of the focused window, so it is read on a short timer, only while a UI is listening.
        _languageTimer = new System.Threading.Timer(_ => _ui.Post(PublishLanguage), null, 1000, 300);
        _running.NativeWindowChanged += (kind, hwnd) =>
        {
            if (_shuttingDown || !_settings.Current.TopBarEnabled) return;
            var foreground = NativeMethods.GetForegroundWindow();
            bool changed = kind == WindowEventKind.Foreground;
            if (!changed && (kind != WindowEventKind.LocationChanged || hwnd != foreground)) return;
            // Batch movement and always inspect the final geometry.
            (_fullscreenTimer ??= new System.Threading.Timer(_ => _ui.Post(PublishFullscreen), null, Timeout.Infinite, Timeout.Infinite)).Change(changed ? 0 : 50, Timeout.Infinite);
        };
    }

    private string? CurrentLanguageTag()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        if (!IsUiProcess((int)pid) && foreground != IntPtr.Zero) _languageSource = foreground;
        else if (NativeMethods.IsWindow(_languageSource)) thread = NativeMethods.GetWindowThreadProcessId(_languageSource, out _);
        if (thread == 0) return null;
        var input = InputLanguages.InputSite(IsUiProcess((int)pid) ? _languageSource : foreground);
        uint inputThread = NativeMethods.GetWindowThreadProcessId(input, out var inputPid);
        if (inputThread != 0 && !IsUiProcess((int)inputPid)) thread = inputThread;
        int id = (int)(GetKeyboardLayout(thread).ToInt64() & 0xffff);
        try { return CultureInfo.GetCultureInfo(id).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch (CultureNotFoundException) { return null; }
    }

    private bool IsUiProcess(int pid) { lock (_uiPids) return pid == Environment.ProcessId || _uiPids.Contains(pid); }

    private void PublishLanguage()
    {
        if (_shuttingDown || _diagnostic || _server.SessionCount == 0 || !_settings.Current.TopBarEnabled) return;
        var tag = CurrentLanguageTag();
        if (tag is null || tag == _languageTag) return;
        _languageTag = tag;
        _server.Publish("language.changed", new JsonObject { ["tag"] = tag });
    }

    private JsonArray MonitorsNode()
    {
        var list = new JsonArray();
        foreach (var m in MonitorHelper.GetMonitors())
            list.Add(new JsonObject
            {
                ["x"] = m.Bounds.Left, ["y"] = m.Bounds.Top, ["w"] = m.Bounds.Width, ["h"] = m.Bounds.Height, ["scale"] = m.Scale,
                ["primary"] = m.IsPrimary, ["name"] = m.DeviceName,
            });
        return list;
    }

    /// <summary>Per monitor: does the foreground window cover it (video, borderless game)? UI windows never count.</summary>
    private JsonObject FullscreenNode()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        if (IsUiProcess((int)pid)) foreground = new IntPtr(_running.ForegroundRoot);
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOTOWNER);
        var list = new JsonArray();
        foreach (var m in MonitorHelper.GetMonitors())
        {
            bool full = FullscreenDetector.IsWindowFullscreenOn(foreground, m.Handle, (uint)Environment.ProcessId)
                || FullscreenDetector.IsWindowFullscreenOn(root, m.Handle, (uint)Environment.ProcessId);
            list.Add(new JsonObject { ["x"] = m.Bounds.Left, ["y"] = m.Bounds.Top, ["fullscreen"] = full });
        }
        return new JsonObject { ["monitors"] = list };
    }

    private void PublishFullscreen()
    {
        if (_shuttingDown || !_settings.Current.TopBarEnabled) return;
        var node = FullscreenNode();
        var text = node.ToJsonString();
        if (text == _fullscreenState) return;
        _fullscreenState = text;
        _server.Publish("fullscreen.changed", node);
    }

    /// <summary>The foreground app's display name for the bar: a pinned app's name, else a tidy name from the window.</summary>
    private string? ForegroundName()
    {
        long foreground = _running.ForegroundRoot;
        var current = _running.Snapshot().FirstOrDefault(w => w.Hwnd == foreground);
        if (current is null) return NativeMethods.GetClass(new IntPtr(foreground)) is "Progman" or "WorkerW" ? "Desktop" : null;
        var pinned = _pinned.Snapshot().FirstOrDefault(p => AppMatching.Matches(p, current));
        if (pinned is not null) return pinned.Name;
        string name = AppMatching.DefaultName(current);
        return name.ToLowerInvariant() switch
        {
            "explorer" => "File Explorer", "floatingdock" => "FloatingDock settings", "taskmgr" => "Task Manager", "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome", "steam" => "Steam", "chatgpt" => "ChatGPT", "code" => "Visual Studio Code",
            _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Desktop",
        };
    }
}
