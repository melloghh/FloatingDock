using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Ipc;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Engine;

/// <summary>What the top bar's menus need from Windows: volume, networks, keyboard layouts, tray icons, power, media.</summary>
internal sealed partial class EngineHost
{
    private readonly HashSet<int> _uiPids = new();
    private IReadOnlyList<WirelessNetworks.Network> _networks = Array.Empty<WirelessNetworks.Network>();
    private IReadOnlyList<TrayItem> _trayItems = Array.Empty<TrayItem>();
    private ShellWorker? _trayWorker;

    private void RegisterSystemHandlers(IpcServer server)
    {
        WindowFilter.ExtraOwnProcess = pid => { lock (_uiPids) return _uiPids.Contains((int)pid); };
        server.SessionHello += s =>
        {
            if (s.ClientPid != 0) lock (_uiPids) _uiPids.Add(s.ClientPid);
        };
        server.SessionClosed += s =>
        {
            if (s.ClientPid != 0) lock (_uiPids) _uiPids.Remove(s.ClientPid);
        };

        // ---- windows owned by the UI that should show up as apps (the settings window)
        server.RegisterSync("windows.include", request =>
        {
            var hwnd = new IntPtr(request.Params["hwnd"]?.GetValue<long>() ?? throw BadParams("hwnd is required."));
            _ui.Post(() => _running.IncludeOwnWindow(hwnd));
            return null;
        });
        server.RegisterSync("windows.exclude", request =>
        {
            var hwnd = new IntPtr(request.Params["hwnd"]?.GetValue<long>() ?? throw BadParams("hwnd is required."));
            _ui.Post(() => _running.ExcludeOwnWindow(hwnd));
            return null;
        });

        // ---- sound
        server.Register("audio.get", async _ => await _worker.Run(() =>
        {
            var s = SystemAudio.Read();
            return (JsonNode?)new JsonObject { ["volume"] = s.Volume, ["muted"] = s.Muted };
        }));
        server.Register("audio.set", async request =>
        {
            float? volume = request.Params["volume"] is { } v ? v.GetValue<float>() : null;
            bool? muted = request.Params["muted"] is { } m ? m.GetValue<bool>() : null;
            await _worker.Run(() =>
            {
                if (volume is { } vol) SystemAudio.SetVolume(vol);
                if (muted is { } mute) SystemAudio.SetMuted(mute);
            });
            return null;
        });

        // ---- network
        server.Register("network.get", async request =>
        {
            bool scan = request.Params["scan"]?.GetValue<bool>() ?? false;
            // Windows' Wi-Fi service can stall; the menu must still answer.
            var result = await Task.Run(() =>
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .Select(n => new
                    {
                        n.Name,
                        Kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet",
                        Address = n.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
                    }).Where(n => n.Address is not null).ToArray();
                return (adapters, wireless: WirelessNetworks.Read(scan));
            }).WaitAsync(TimeSpan.FromSeconds(12));
            _networks = result.wireless.Networks;
            var adapterList = new JsonArray();
            foreach (var a in result.adapters) adapterList.Add(new JsonObject { ["name"] = a.Name, ["kind"] = a.Kind, ["address"] = a.Address });
            var networks = new JsonArray();
            foreach (var n in result.wireless.Networks)
                networks.Add(new JsonObject
                {
                    ["name"] = n.Name, ["signal"] = n.Signal, ["connected"] = n.Connected, ["secured"] = n.Secured,
                    ["available"] = n.Available, ["saved"] = n.Profile.Length > 0,
                });
            return new JsonObject { ["adapters"] = adapterList, ["networks"] = networks, ["error"] = result.wireless.Error };
        });
        server.Register("network.toggle", async request =>
        {
            var name = request.Params["name"]?.GetValue<string>() ?? throw BadParams("name is required.");
            var network = _networks.FirstOrDefault(n => n.Name == name) ?? throw BadParams("Unknown network. Refresh the list.");
            if (!network.Connected && network.Profile.Length == 0) return new JsonObject { ["needsWindows"] = true };
            await Task.Run(() => { if (network.Connected) WirelessNetworks.Disconnect(network); else WirelessNetworks.ConnectSaved(network); });
            return new JsonObject { ["needsWindows"] = false };
        });

        // ---- keyboard layouts
        server.RegisterSync("language.list", request =>
        {
            // The layout belongs to the window the user was working in, not to the menu that was just opened.
            var target = new IntPtr(request.Params["target"]?.GetValue<long>() ?? _running.ForegroundRoot);
            uint thread = NativeMethods.GetWindowThreadProcessId(InputLanguages.InputSite(target), out _);
            var current = InputLanguages.GetKeyboardLayout(thread);
            var list = new JsonArray();
            foreach (var l in InputLanguages.Installed())
                list.Add(new JsonObject { ["handle"] = l.Handle.ToInt64(), ["name"] = l.Name, ["tag"] = l.Tag, ["current"] = l.Handle == current });
            return new JsonObject { ["target"] = target.ToInt64(), ["layouts"] = list };
        });
        server.RegisterSync("language.set", request =>
        {
            var target = new IntPtr(request.Params["target"]?.GetValue<long>() ?? throw BadParams("target is required."));
            var handle = new IntPtr(request.Params["handle"]?.GetValue<long>() ?? throw BadParams("handle is required."));
            NativeMethods.SetForegroundWindow(target);
            return new JsonObject { ["ok"] = InputLanguages.Request(target, handle) };
        });

        // ---- tray icons
        server.Register("tray.items", async _ =>
        {
            TrayOperation(true);
            try
            {
                _trayItems = await (_trayWorker ??= new ShellWorker()).Run(SystemTrayOverflow.ReadItems);
            }
            catch (Exception ex) { EngineLog.Warn("Tray icons unavailable: " + ex.Message); _trayItems = Array.Empty<TrayItem>(); }
            finally { TrayOperation(false); }
            var list = new JsonArray();
            for (int i = 0; i < _trayItems.Count; i++)
            {
                var t = _trayItems[i];
                list.Add(new JsonObject
                {
                    ["index"] = i, ["name"] = t.Name, ["icon"] = t.Icon is null ? null : Convert.ToBase64String(t.Icon),
                    ["iconPixels"] = TrayIconImage.PixelSize(t.Icon),
                });
            }
            return new JsonObject { ["items"] = list };
        });
        server.Register("tray.activate", async request =>
        {
            int index = request.Params["index"]?.GetValue<int>() ?? -1;
            if (index < 0 || index >= _trayItems.Count) throw BadParams("That tray icon is no longer available.");
            var item = _trayItems[index];
            bool context = request.Params["context"]?.GetValue<bool>() ?? false;
            POINT? anchor = request.Params["x"] is { } x && request.Params["y"] is { } y ? new POINT { X = x.GetValue<int>(), Y = y.GetValue<int>() } : null;
            TrayOperation(true);
            try { return new JsonObject { ["ok"] = await (_trayWorker ??= new ShellWorker()).Run(() => SystemTrayOverflow.ActivateItem(item, context, anchor)) }; }
            catch (Exception ex) { EngineLog.Warn("Tray action failed: " + ex.Message); return new JsonObject { ["ok"] = false }; }
            finally { TrayOperation(false); }
        });

        // ---- power and shell
        server.RegisterSync("power.run", request =>
        {
            var action = request.Params["action"]?.GetValue<string>();
            if (action is not ("shutdown" or "restart")) throw BadParams("action must be shutdown or restart.");
            // A diagnostic engine must never power off the machine it is being tested on.
            if (_diagnostic) return new JsonObject { ["ran"] = false };
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"))
                { Arguments = action == "restart" ? "/r /t 0" : "/s /t 0", UseShellExecute = false, CreateNoWindow = true });
                return new JsonObject { ["ran"] = true };
            }
            catch (Exception ex)
            {
                EngineLog.Warn("Power action failed: " + ex.Message);
                Notify("FloatingDock", "Could not start the power action.", true);
                return new JsonObject { ["ran"] = false };
            }
        });
        server.RegisterSync("shell.execute", request =>
        {
            var uri = request.Params["uri"]?.GetValue<string>() ?? throw BadParams("uri is required.");
            // Only the Windows settings pages the menus link to; never an arbitrary command.
            if (!(uri.StartsWith("ms-settings:", StringComparison.Ordinal) || uri == "ms-availablenetworks:")) throw BadParams("That link is not allowed.");
            if (!_diagnostic) ShellLauncher.Execute(uri, null, null);
            return null;
        });
        server.RegisterSync("windows.activate", request =>
        {
            var hwnd = new IntPtr(request.Params["hwnd"]?.GetValue<long>() ?? throw BadParams("hwnd is required."));
            WindowStyler.BringToFront(hwnd);
            return null;
        });
    }
}
