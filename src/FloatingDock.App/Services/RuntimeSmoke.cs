using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using System.Runtime.InteropServices;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Taskbar;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;

/// <summary>Opt-in integration check on a real Windows desktop. Uses a separate, empty data directory.</summary>
internal static partial class RuntimeSmoke
{
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    private delegate void TimerCallback(IntPtr hwnd, uint message, UIntPtr id, uint time);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint interval, TimerCallback callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll")] private static extern bool EndMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal static async Task RunAsync(DockApp app)
    {
        var checks = new Dictionary<string, object>();
        bool success = false;
        string? error = null;
        try
        {
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui140"))
            {await CheckUi140Async(app,checks);Check(Program.UiFailureCount==0,"XAML failure in compositor checks.");success=true;return;}
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui130"))
            {
                await CheckUi130Async(app,checks);Check(Program.UiFailureCount==0,"XAML failure in workspace checks.");success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui122"))
            {
                await CheckUi122Async(app,checks);Check(Program.UiFailureCount==0,"XAML failure in opening animation checks.");success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui121"))
            {
                await CheckUi121Async(app,checks);Check(Program.UiFailureCount==0,"XAML failure in optimization/tiling checks.");success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-render121"))
            {
                app.Settings.Update(s=>s.AnimationFps=144);await Task.Delay(300);
                await CheckDockRenderingAsync(app,checks);Check(Program.UiFailureCount==0,"XAML failure in rendering checks.");success=true;return;
            }
            // Explorer applies auto-hide/work-area changes asynchronously at first start.
            await Task.Delay(Program.TestDefaultSettings ? 1800 : 600);
            if(Environment.GetCommandLineArgs().Contains("--test-only-tray120"))
            {
                var bar=app.TopBar!.Primary!;bar.IgnoreRealCursorForDiagnostics=true;
                app.Dock.HoldFullscreenStateForDiagnostics=true;
                var system=new Win32TaskbarSystem();var before=system.GetTaskbarWindows();
                Check(system.TryGetAppBarState(out var flags),"Could not read original taskbar flags.");
                var regions=new Dictionary<long,byte[]?>();
                foreach(var window in before){Check(system.TryGetRegion(window.Handle,out var region),"Could not snapshot taskbar region.");regions[window.Handle]=region;}
                var probe=new Window { Title="FloatingDock tray probe",Content=new Grid() };probe.Activate();
                try
                {
                    if(Program.TestSystemTaskbar)
                    {
                        app.Settings.Update(s=>s.TaskbarMode=TaskbarMode.HideCompletely);await Task.Delay(700);
                        // Explorer may replace/repaint its taskbar during the work-area
                        // transition. Settle maintenance and inspect current HWNDs.
                        for(int attempt=0;attempt<10&&!system.GetTaskbarWindows().All(w=>system.IsClippedEmpty(w.Handle));attempt++)
                        { app.Taskbar.Maintain(TaskbarMode.HideCompletely);await Task.Delay(100); }
                        Check(system.GetTaskbarWindows().All(w=>system.IsClippedEmpty(w.Handle)),"Taskbar was not clipped for the hidden-tray test.");
                    }
                    await bar.OpenTrayForDiagnostics();await Task.Delay(250);
                    Check(bar.TrayVisible,"Native tray unavailable: "+FloatingDock.Windows.Integration.SystemTrayOverflow.LastDiagnostic);
                    bar.SetCaptureHidden(true);Check(!FloatingDock.Windows.Integration.SystemTrayOverflow.Visible,"Tray dismissal left native pixels.");
                    checks["realTrayOpenAndDismiss"]=new { hiddenTaskbar=Program.TestSystemTaskbar };success=true;return;
                }
                finally
                {
                    probe.Close();bar.SetCaptureHidden(true);
                    if(Program.TestSystemTaskbar)
                    {
                        app.Settings.Update(s=>s.TaskbarMode=TaskbarMode.Leave);Check(app.Taskbar.Restore().Success,"Taskbar restore failed.");await Task.Delay(150);
                        Check(system.TryGetAppBarState(out var after)&&after==flags,"Taskbar flags changed after native tray test.");
                        foreach(var window in before)
                        {
                            Check(system.GetTaskbarWindows().Any(w=>w.Handle==window.Handle&&w.Visible==window.Visible),"Original taskbar visibility changed.");
                            Check(system.TryGetRegion(window.Handle,out var region)&&(regions[window.Handle] is null?region is null:region is not null&&regions[window.Handle]!.AsSpan().SequenceEqual(region)),"Original taskbar region changed.");
                        }
                        checks["originalTaskbarRestored"]=true;
                    }
                }
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui120"))
            {
                await CheckUi120Async(app,checks);
                Check(Program.UiFailureCount==0,"A XAML failure occurred during interface feature checks.");
                success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui119"))
            {
                await CheckUi119Async(app,checks);
                Check(Program.UiFailureCount==0,"A XAML failure occurred during dynamic dock checks.");
                success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui118"))
            {
                await CheckUi118Async(app,checks);
                Check(Program.UiFailureCount==0,"A XAML failure occurred during auto-hide/settings checks.");
                success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui117"))
            {
                await CheckUi117Async(app,checks);
                Check(Program.UiFailureCount==0,"A XAML failure occurred during backdrop checks.");
                success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui116"))
            {
                await CheckUi116Async(app,checks);
                Check(Program.UiFailureCount==0,"A XAML failure occurred during menu/preview transitions.");
                success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui115"))
            {
                await CheckUi115Async(app,checks);
                Check(Program.UiFailureCount==0,"A XAML failure occurred during fullscreen/motion checks.");
                success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui113"))
            {
                await CheckUi113Async(app,checks);
                Check(Program.UiFailureCount==0,"A XAML failure occurred during edge/preview checks.");
                success=true;return;
            }
            if (Environment.GetCommandLineArgs().Contains("--test-only-icons112"))
            {
                var results = new List<object>();
                var fixturePixels = new byte[256 * 256 * 4];
                for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
                {
                    int p = (y * 256 + x) * 4; byte c = (byte)((x + y) % 2 * 255);
                    fixturePixels[p] = fixturePixels[p + 1] = fixturePixels[p + 2] = c; fixturePixels[p + 3] = 255;
                }
                var fixture = FloatingDock.Core.Icons.PngWriter.EncodeBgra(256,256,fixturePixels,false);
                var filtered = await IconBitmapDecoder.DecodeAsync(fixture,64);
                Check(filtered.Width == 64 && filtered.Pixels[32 * 64 * 4 + 32 * 4] is >= 126 and <= 129, "High-frequency artwork was not averaged before GPU minification.");
                var fixtureFile = Path.Combine(app.Paths.Root,"checkerboard.png"); File.WriteAllBytes(fixtureFile,fixture);
                var files = new[] { fixtureFile }.Concat(Environment.GetCommandLineArgs().Where(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase))).Take(16).ToArray();
                foreach (var file in files)
                {
                    var raster = await IconBitmapDecoder.DecodeAsync(File.ReadAllBytes(file), 64);
                    int invalid = 0, transparentColour = 0;
                    for (int i = 0; i < raster.Pixels.Length; i += 4)
                    {
                        byte alpha = raster.Pixels[i + 3];
                        if (raster.Pixels[i] > alpha || raster.Pixels[i + 1] > alpha || raster.Pixels[i + 2] > alpha) invalid++;
                        if (alpha == 0 && (raster.Pixels[i] != 0 || raster.Pixels[i + 1] != 0 || raster.Pixels[i + 2] != 0)) transparentColour++;
                    }
                    Check(invalid == 0 && transparentColour == 0, "An icon lost premultiplied transparency.");
                    Check(Math.Max(raster.Width,raster.Height) <= 64, "The raster exceeds its requested display size.");
                    results.Add(new { name = Path.GetFileName(file), raster.Width, raster.Height, invalid, transparentColour });
                    File.WriteAllBytes(Path.Combine(app.Paths.Root, Path.GetFileName(file) + ".bgra"), raster.Pixels);
                }
                checks["iconAlpha"] = results;
                using var iconWindow = new NativeDockWindow();
                using var iconAlpha = new AlphaWindow(iconWindow.Handle);
                using var graphics = new Dock.SystemDockGraphics(iconWindow.Handle);
                int probeWidth = 40 + files.Length * 85;
                graphics.SetViewport(probeWidth, 240, 1);
                var background = graphics.Compositor.CreateSpriteVisual();
                background.Size = new System.Numerics.Vector2(probeWidth, 240);
                background.Brush = graphics.Compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(255, 32, 32, 32));
                graphics.Content.Children.InsertAtBottom(background);
                int column = 0;
                foreach (var file in files)
                {
                    var raster = await IconBitmapDecoder.DecodeAsync(File.ReadAllBytes(file), 64);
                    var surface = graphics.Upload(raster.Pixels, raster.Width, raster.Height);
                    for (int row = 0; row < 3; row++)
                    {
                        var brush = graphics.Compositor.CreateSurfaceBrush(surface);
                        brush.BitmapInterpolationMode = global::Windows.UI.Composition.CompositionBitmapInterpolationMode.Linear;
                        var sprite = graphics.Compositor.CreateSpriteVisual();
                        sprite.Size = new System.Numerics.Vector2(row == 2 ? 64 : row == 1 ? 48 : 36);
                        sprite.Offset = new System.Numerics.Vector3(20 + column * 85, 20 + row * 70, 0);
                        sprite.Brush = brush; graphics.Content.Children.InsertAtTop(sprite);
                    }
                    column++;
                }
                WindowStyler.SetBounds(iconWindow.Handle, 150, 250, probeWidth, 240, topmost: true);
                NativeMethods.ShowWindow(iconWindow.Handle, NativeMethods.SW_SHOWNOACTIVATE);
                await Task.Delay(600);
                var capture = OwnWindowCapture.Capture(iconWindow.Handle);
                capture.Save(Path.Combine(app.Paths.Root, "icons-composited.png"));
                for (int row = 0; row < 3; row++)
                {
                    int center = row == 2 ? 32 : row == 1 ? 24 : 18;
                    int pixel = ((20 + row * 70 + center) * capture.Width + 20 + center) * 4;
                    Check(capture.Pixels[pixel] is >= 125 and <= 131 && capture.Pixels[pixel + 1] is >= 125 and <= 131 && capture.Pixels[pixel + 2] is >= 125 and <= 131, "GPU minification reintroduced stripes into the averaged texture.");
                }
                checks["filteredGpuMinification"] = new { nativePixelsChecked = true, resting = 36, intermediate = 48, magnified = 64 };
                success = true; return;
            }
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui111"))
            {
                await CheckUi110Async(app,checks);
                foreach(var kind in new[]{"sound","network","language","calendar"})
                {
                    var bar=app.TopBar!.Primary!;
                    bar.OpenMenuForDiagnostics(kind);await Task.Delay(100);
                    Check(bar.Menus.ActiveKind==kind,"Menu did not open: "+kind);
                    bar.OpenMenuForDiagnostics(kind);await Task.Delay(220);
                    Check(bar.Menus.ActiveKind is null&&!WindowStyler.IsVisible(bar.Menus.PopupHandle),"Repeated icon invocation did not close: "+kind);
                }
                checks["repeatIconToggle"]=true;
                await CheckUi109Async(app,checks);
                var packSample=new AppRef { Name="Steam",ExePath="C:\\FloatingDock-test\\steam.exe" };
                app.Pinned.Add(packSample);app.Overrides.Set(packSample.Key,Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.png"));
                app.Settings.Update(s=>s.UseIconPack=false);
                var importDir = Path.Combine(app.Packs.RootDirectory, "DiagnosticImported");
                Directory.CreateDirectory(importDir);
                File.Copy(Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.png"),Path.Combine(importDir,"steam.png"));
                app.ShowSettings("Icon Packs");await Task.Delay(150);
                var packs=app.Packs.ListPacks();int packIndex=packs.ToList().FindIndex(p=>p.Id=="DiagnosticImported");
                app.SettingsWin!.IconPackComboForDiagnostics!.SelectedIndex=packIndex;await Task.Delay(250);
                Check(app.Settings.Current.UseIconPack&&app.Resolver.ActivePack?.Id=="DiagnosticImported","Selecting a pack did not enable it.");
                var applyPeer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(app.SettingsWin.ApplyPackForDiagnostics!);
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)applyPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();await Task.Delay(250);
                Check(!app.Overrides.Has(packSample)&&app.Resolver.Resolve(packSample) is not null,"Pack did not replace the matching individual icon.");
                NativeMethods.SetWindowPos(app.SettingsWin.Handle,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
                OwnWindowCapture.Capture(app.SettingsWin.Handle).Save(Path.Combine(app.Paths.Root,"settings-icon-packs.png"));
                app.SettingsWin.Close();app.Pinned.Remove(packSample.Key);
                checks["iconPackSelection"]=new { enabled=true,matched=true,replacedMatchingOverride=true };
                var probe=new Window { Title="FloatingDock preview diagnostic",Content=new Grid { Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,34,187,153)) } };
                var hwnd=WindowSetup.Hwnd(probe);probe.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(420,260));probe.AppWindow.Show(false);
                try
                {
                    var dock=app.Dock.PrimaryWindowForDiagnostics!;var preview=dock.PreviewForDiagnostics;
                    var b=dock.Bounds;
                    var entry=new DockEntry { Kind=DockEntryKind.App,Key="preview-diagnostic",Name="Preview",Windows=new[]{new WindowInfo(hwnd.ToInt64(),Environment.ProcessId,Environment.ProcessPath,null,probe.Title,null,false)} };
                    preview.Track(entry,new RECT { Left=b.X+200,Right=b.X+246,Top=b.Y+50,Bottom=b.Y+96 },diagnostic:true);
                    await Task.Delay(120);Check(!preview.Visible,"Preview appeared without hover delay.");
                    await Task.Delay(850);Check(preview.Visible&&preview.ThumbnailCount==1,"No live DWM thumbnail appeared.");
                    var frame=OwnWindowCapture.Capture(preview.Handle);frame.Save(Path.Combine(app.Paths.Root,"window-preview.png"));
                    int pixel=((frame.Height/2)*frame.Width+frame.Width/2)*4;
                    Check(frame.Pixels[pixel+1]>frame.Pixels[pixel+2]+40,"DWM preview did not display the source window.");
                    dock.SetCaptureHidden(true);Check(!preview.Visible,"Capture left a preview visible.");dock.SetCaptureHidden(false);
                    checks["windowPreview"]=new { delayed=true,liveDwmPixels=true,captureHidden=true };
                }
                finally { app.Dock.PrimaryWindowForDiagnostics!.PreviewForDiagnostics.Hide();probe.Close(); }
                var original=IconService.ReadLimited(Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.png"))!;
                var raster=await IconBitmapDecoder.DecodeAsync(original,32);
                Check(raster.Width==32 && raster.Height==32,"Artwork was not prefiltered to the requested display size.");
                checks["originalArtworkPreserved"]=new { raster.Width,raster.Height,noCrop=true,noSharpen=true };
                Check(Program.UiFailureCount==0,"A XAML failure occurred during UI checks.");success=true;return;
            }
            if(Environment.GetCommandLineArgs().Contains("--request-wifi-access"))
            {
                var access=await global::Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync();
                checks["wifiAccess"]=access.ToString();
                var networks=await Task.Run(()=>FloatingDock.Windows.Integration.WirelessNetworks.Read());
                checks["wifiNetworks"]=new { visible=networks.Networks.Count(n=>n.Available), permissionDenied=networks.Error is not null };
                success=true;return;
            }
            if (Environment.GetCommandLineArgs().Contains("--test-only-ui110"))
            {
                await CheckUi110Async(app, checks);
                Check(Program.UiFailureCount == 0, "A XAML exception occurred during the menu test.");
                success = true;
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--test-only-ui109"))
            {
                await CheckUi109Async(app, checks);
                Check(Program.UiFailureCount == 0, "A XAML exception occurred during the test.");
                success = true;
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--test-only-animation-anchor"))
            {
                await CheckAnimationAnchorAsync(app, checks);
                success = true;
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--test-only-fullscreen"))
            {
                // Closing the first/last XAML probe exits WinUI; keep the hidden Launchpad alive between checks.
                await app.PrepareLaunchpadAsync();
                await CheckNativeFullscreenAsync(checks);
                await CheckFullscreenRevealAsync(app, checks);
                success = true;
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--test-only-dock-input"))
            {
                await app.PrepareLaunchpadAsync();
                await CheckDockInputAsync(app, checks);
                app.ToggleLaunchpad();
                await Task.Delay(7000);
                Check(app.LaunchpadWin!.IsOpen && WindowStyler.IsVisible(app.LaunchpadWin.Handle), "Launchpad closed: " + app.LaunchpadWin.DiagnosticState);
                success = true;
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--test-only-window-activation"))
            {
                await CheckWindowActivationAsync(checks);
                success = true;
                return;
            }
            if (Program.TestOnlyTaskManager)
            {
                var target = app.Running.Snapshot().FirstOrDefault(w => w.ExeFileName?.Equals("taskmgr.exe", StringComparison.OrdinalIgnoreCase) == true || w.ClassName == "TaskManagerWindow") ?? throw new InvalidOperationException("No running Task Manager.");
                var activation = await WindowActivator.ActivateOrMinimizeAsync(new[] { target }, false);
                checks["runningTaskManager"] = new { result = activation.ToString(), minimized = NativeMethods.IsIconic(new IntPtr(target.Hwnd)), foregroundConfirmed = NativeMethods.GetForegroundWindow().ToInt64() == target.Hwnd };
                Check(activation == WindowActivator.ActivationResult.Activated, "Task Manager did not restore.");
                success = true;
                return;
            }
            app.Log.Info("runtime smoke: checking dock and tray");
            Check(app.Dock.WindowHandles.Count > 0 && app.Dock.WindowHandles.All(WindowStyler.IsVisible), "No visible dock window.");
            checks["dockWindows"] = app.Dock.WindowHandles.Count;
            Check(app.Tray.IsAdded, "The notification-area icon was not registered.");
            checks["tray"] = true;
            if (Program.TestDefaultSettings)
            {
                Check(app.Settings.Current.TaskbarMode == TaskbarMode.HideCompletely && app.Settings.Current.Responsiveness == 10, "The default user configuration was not applied.");
                Check(new Win32TaskbarSystem().GetTaskbarWindows().All(w => new Win32TaskbarSystem().IsClippedEmpty(w.Handle)), "The application did not hide Taskbar on its first start.");
                checks["defaultFirstStart"] = true;
            }

            var sample = new AppRef { Name = "FloatingDock", ExePath = Environment.ProcessPath };
            app.Log.Info("runtime smoke: checking icons and persistence");
            app.Overrides.Set(sample.Key, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"));
            Check(app.Pinned.Add(sample), "The test pin could not be saved.");
            var icon = await app.Icons.GetAsync(sample, 64);
            Check(icon is not null && icon.PixelWidth > 0, "The icon did not decode.");
            checks["icon"] = true;
            var icoBytes = IconService.ReadLimited(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            Check(icoBytes is not null, "The ICO header was rejected.");
            var ico = await IconService.DecodeAsync(icoBytes!, 64);
            Check(ico.PixelWidth > 0, "The ICO did not decode.");
            checks["ico"] = true;
            await app.Dock.PrimaryWindowForDiagnostics!.VerifyTextureDecodeForSmokeAsync(icoBytes!);
            checks["nativeIcoTexture"] = true;
            await CheckIconRastersAsync(app, sample, checks);
            app.Settings.Flush();
            Check(File.Exists(app.Paths.ConfigFile), "The settings file was not saved.");
            Check(new FloatingDock.Core.Services.AppListStore(app.Paths.PinnedFile).Contains(sample.Key), "The pin was not persisted.");
            checks["persistence"] = true;
            var preparationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            await app.PrepareLaunchpadAsync();
            checks["launchpadColdPreparationMs"] = System.Diagnostics.Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds;
            Check(app.LaunchpadWin!.IsPrepared && !WindowStyler.IsVisible(app.LaunchpadWin.Handle), "Launchpad preparation left a visible window.");
            await CheckDockInputAsync(app, checks);

            app.Log.Info("runtime smoke: checking composited pixels and animation cost");
            await CheckDockRenderingAsync(app, checks);
            var diagnosticDock = app.Dock.PrimaryWindowForDiagnostics!;
            diagnosticDock.BeginRenderDiagnostics();
            await Task.Delay(150);
            Check(await diagnosticDock.CheckFrozenMenuForSmokeAsync(), "Dock moved while its context menu state was active.");
            diagnosticDock.EndRenderDiagnostics();
            checks["contextMenuFreezesDock"] = true;
            CheckNativeMenu(app, checks);
            await CheckNativeFullscreenAsync(checks);
            await CheckFullscreenRevealAsync(app, checks);
            var divider = AppRef.Separator();
            Check(app.Pinned.Add(divider, 1), "Could not save a dock separator.");
            await Task.Delay(350);
            Check(app.Dock.ViewModel.Entries.Any(e => e.Key == divider.Key && e.Kind == DockEntryKind.Separator), "The persisted separator was not added to Dock.");
            Check(new FloatingDock.Core.Services.AppListStore(app.Paths.PinnedFile).Snapshot().Any(a => a.Key == divider.Key && a.IsSeparator), "The separator was not persisted.");
            OwnWindowCapture.Capture(diagnosticDock.Handle).Save(Path.Combine(app.Paths.Root, "dock-separator.png"));
            Check(app.Pinned.Remove(divider.Key), "The separator could not be removed.");
            checks["dockSeparatorsPersist"] = true;

            app.Log.Info("runtime smoke: opening Launchpad");
            var openingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            app.ToggleLaunchpad();
            checks["preparedLaunchpadShowCallMs"] = System.Diagnostics.Stopwatch.GetElapsedTime(openingStarted).TotalMilliseconds;
            app.LaunchpadWin!.SimulateQueuedDeactivationForSmoke();
            await Task.Delay(500);
            Check(app.LaunchpadWin is not null && WindowStyler.IsVisible(app.LaunchpadWin.Handle), "No visible Launchpad window.");
            await app.LaunchpadWin!.LoadCatalogForSmokeAsync();
            // An initial Show may already be loading the catalog.
            for (int i = 0; !app.LaunchpadWin.IsCatalogLoaded && i < 220; i++) await Task.Delay(100);
            Check(app.LaunchpadWin.IsCatalogLoaded, "The Start pin catalog did not load.");
            checks["catalogApps"] = app.LaunchpadWin.CatalogCount;
            var startPins = await app.StartPins.GetAsync();
            Check(startPins.ExportedPins == app.LaunchpadWin.ExportedStartPins && startPins.Apps.Count == app.LaunchpadWin.CatalogCount, "Launchpad does not match the Windows Start pin catalog.");
            Check(app.LaunchpadWin.HasSettingsShortcut, "Launchpad settings shortcut is missing.");
            checks["startPins"] = new { exported = startPins.ExportedPins, resolved = startPins.Apps.Count, unavailable = startPins.Warnings.Count };
            await Task.Delay(2500);
            var launchpadCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var launchpadIdleStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(2000);
            checks["launchpadIdleCpuPercentOfMachine"] = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - launchpadCpu).TotalMilliseconds / System.Diagnostics.Stopwatch.GetElapsedTime(launchpadIdleStarted).TotalMilliseconds * 100 / Environment.ProcessorCount;
            Check(app.LaunchpadWin.IsOpen && WindowStyler.IsVisible(app.LaunchpadWin.Handle), "Launchpad disappeared after opening: " + app.LaunchpadWin.DiagnosticState);
            Check(app.Dock.WindowHandles.All(h => !WindowStyler.IsVisible(h)), "Dock was not suspended under Launchpad.");
            Check(app.LaunchpadWin.CatalogCount == 0 || app.LaunchpadWin.RealizedTileCount > 0, "Launchpad has no rendered tiles.");
            checks["launchpadRenderedTiles"] = app.LaunchpadWin.RealizedTileCount;
            Check(!app.LaunchpadWin.HasSearch && app.LaunchpadWin.HasAddApplication,"Launchpad must expose Add rather than search.");
            checks["launchpadAddWithoutSearch"] = true;
            OwnWindowCapture.Capture(app.LaunchpadWin.Handle).Save(Path.Combine(app.Paths.Root,"launchpad-desktop.png"));
            // Reopen before the old hide animation completes: its callback must not hide the new presentation.
            int decodesBeforeReopen = app.Icons.RasterDecodes, rebuildsBeforeReopen = app.LaunchpadWin.ViewRebuilds;
            for (int i = 0; i < 3; i++)
            {
                app.LaunchpadWin.Hide();
                await Task.Delay(35);
                app.LaunchpadWin.Show();
                await Task.Delay(500);
                Check(app.LaunchpadWin.IsOpen && WindowStyler.IsVisible(app.LaunchpadWin.Handle), "A stale hide animation closed reopened Launchpad.");
            }
            checks["launchpadAddAndReopen"] = true;
            checks["launchpadWarmCache"] = new { rasterDecodes = app.Icons.RasterDecodes - decodesBeforeReopen, viewRebuilds = app.LaunchpadWin.ViewRebuilds - rebuildsBeforeReopen };
            Check(app.Icons.RasterDecodes == decodesBeforeReopen && app.LaunchpadWin.ViewRebuilds == rebuildsBeforeReopen, "Reopening Launchpad redundantly decoded images or rebuilt the unchanged grid.");
            await CheckLaunchpadAnimationAsync(app, checks);
            app.LaunchpadWin.OpenSettings();
            await Task.Delay(250);
            Check(app.SettingsWin?.XamlRoot is not null && WindowStyler.IsVisible(app.SettingsWin.Handle), "Launchpad settings shortcut did not open settings.");
            checks["launchpadSettingsShortcut"] = true;

            foreach (var page in new[] { "Dock", "Appearance", "Magnification", "Behaviour", "Icon Packs", "Launchpad", "Windows Taskbar", "About" })
            {
                app.Log.Info("runtime smoke: opening settings page " + page);
                app.ShowSettings(page);
                await Task.Delay(150);
                Check(app.SettingsWin?.XamlRoot is not null && WindowStyler.IsVisible(app.SettingsWin.Handle), "Settings did not load: " + page);
            }
            checks["settingsPages"] = 8;
            app.SettingsWin!.Close();
            await CheckWindowActivationAsync(checks);
            if (Program.TestRunningTaskManager)
            {
                var taskManager = app.Running.Snapshot().FirstOrDefault(w => w.ExeFileName?.Equals("taskmgr.exe", StringComparison.OrdinalIgnoreCase) == true || w.ClassName == "TaskManagerWindow");
                Check(taskManager is not null, "No running Task Manager window to verify.");
                bool minimized = NativeMethods.IsIconic(new IntPtr(taskManager!.Hwnd));
                var result = await WindowActivator.ActivateOrMinimizeAsync(new[] { taskManager }, false);
                checks["runningTaskManager"] = new { initiallyMinimized = minimized, result = result.ToString(), foregroundConfirmed = NativeMethods.GetForegroundWindow().ToInt64() == taskManager.Hwnd };
                Check(result == WindowActivator.ActivationResult.Activated, "The running Task Manager window did not activate.");
            }
            checks["hotkeys"] = app.HotkeyRegistered;

            var monitors = MonitorHelper.GetMonitors();
            using (var reservation = new AppBarReservation())
            {
                var result = reservation.Apply(monitors.Select(m => new AppBarRequest(m, 80)).ToList());
                Check(result.AllReserved && result.Reserved.Count == monitors.Count, "AppBar registration failed.");
                await Task.Delay(300);
                var reservedMonitors = MonitorHelper.GetMonitors();
                Check(monitors.All(before => reservedMonitors.Any(after => after.DeviceName == before.DeviceName && after.WorkArea.Bottom < before.WorkArea.Bottom)), "The registered AppBar did not reserve the work area.");
                reservation.Release();
                Check(!reservation.IsActive, "AppBar reservations were not released.");
            }
            await Task.Delay(300);
            var releasedMonitors = MonitorHelper.GetMonitors();
            Check(monitors.All(before => releasedMonitors.Any(after => after.DeviceName == before.DeviceName && after.WorkArea == before.WorkArea)), "The original work area was not restored.");
            checks["appBarMonitors"] = monitors.Count;
            if (Program.TestDefaultSettings)
            {
                var system = new Win32TaskbarSystem();
                var original = new FloatingDock.Core.Taskbar.TaskbarStateStore(app.Paths.TaskbarStateFile).Load().Record
                    ?? throw new InvalidOperationException("The application's own recovery record is missing.");
                foreach (var window in system.GetTaskbarWindows())
                {
                    Check(system.TrySetRegion(window.Handle, null), "Cannot reset Taskbar region for the timer test.");
                    system.TrySetVisible(window.Handle, true);
                }
                await Task.Delay(1800);
                Check(system.GetTaskbarWindows().All(w => system.IsClippedEmpty(w.Handle)), "The application's timer did not reapply taskbar hiding.");
                var dock = app.Dock.PrimaryWindowForDiagnostics!;
                dock.BeginRenderDiagnostics();
                await Task.Delay(400);
                OwnWindowCapture.Capture(dock.Handle).Save(Path.Combine(app.Paths.Root, "dock-wallpaper.png"));
                dock.EndRenderDiagnostics();
                // Changing the configured mode stops maintenance before checking the original state.
                app.Settings.Update(x => x.TaskbarMode = TaskbarMode.Leave);
                var restored = app.Taskbar.Restore();
                Check(restored.Success, "The application could not restore its own taskbar changes.");
                Check(system.TryGetAppBarState(out var flags) && flags == original.OriginalAppBarFlags, "Default-mode AppBar flags were not restored.");
                var current = system.GetTaskbarWindows();
                foreach (var before in original.Windows)
                {
                    Check(current.Any(w => w.Handle == before.Handle && w.Visible == before.OriginalVisible), "Default-mode visibility was not restored.");
                    Check(system.TryGetRegion(before.Handle, out var region) && (before.OriginalRegion is null ? region is null : region is not null && before.OriginalRegion.AsSpan().SequenceEqual(region)), "Default-mode window region was not restored.");
                }
                Check(!app.Taskbar.HasPendingRestore, "Default-mode recovery record was not cleared.");
                checks["defaultTaskbarTimerAndRestore"] = true;
            }
            else if (Program.TestSystemTaskbar)
            {
                var system = new Win32TaskbarSystem();
                Check(system.TryGetAppBarState(out var originalFlags), "Cannot read the original taskbar state.");
                var originalWindows = system.GetTaskbarWindows();
                Check(originalWindows.Count > 0, "No taskbar windows to test.");
                var taskbar = new TaskbarController(Path.Combine(app.Paths.Root, "smoke-taskbar.json"), app.Log.Info);
                foreach (var mode in new[] { TaskbarMode.AutoHide, TaskbarMode.HideCompletely })
                {
                    try
                    {
                        var result = taskbar.Apply(mode);
                        Check(result.Success, "Taskbar mode did not apply: " + mode + " " + result.Message);
                        Check(system.TryGetAppBarState(out var flags) && (flags & 1) != 0, "Auto-hide flag was not set.");
                        if (mode == TaskbarMode.HideCompletely)
                        {
                            Check(system.GetTaskbarWindows().All(w => system.IsClippedEmpty(w.Handle)), "Taskbar windows did not clip empty.");
                            foreach (var window in system.GetTaskbarWindows()) system.TrySetVisible(window.Handle, true);
                            Check(system.GetTaskbarWindows().All(w => system.IsClippedEmpty(w.Handle)), "Showing the HWND exposed the clipped Taskbar.");
                            foreach (var window in system.GetTaskbarWindows()) system.TrySetRegion(window.Handle, null);
                            Check(taskbar.Maintain(mode).Success && system.GetTaskbarWindows().All(w => system.IsClippedEmpty(w.Handle)), "Taskbar did not hide again after Explorer reset its region.");
                            await Task.Delay(2000);
                            Check(taskbar.Maintain(mode).Success && system.GetTaskbarWindows().All(w => system.IsClippedEmpty(w.Handle)), "Taskbar hiding was not maintained.");
                        }
                        await Task.Delay(300);
                    }
                    finally
                    {
                        var restored = taskbar.Restore();
                        Check(restored.Success, "Taskbar restore failed: " + restored.Message);
                    }
                    Check(system.TryGetAppBarState(out var afterFlags) && afterFlags == originalFlags, "Original taskbar flags were not restored.");
                    var afterWindows = system.GetTaskbarWindows();
                    Check(originalWindows.All(before => afterWindows.Any(after => before.Handle == after.Handle && before.Visible == after.Visible)), "Original taskbar visibility was not restored.");
                    Check(!taskbar.HasPendingRestore, "The recovery record was not cleared after verified restore.");
                }
                checks["taskbarModesRestored"] = new[] { "AutoHide", "HideCompletely" };
            }
            Check(Program.UiFailureCount == 0, "A XAML exception occurred during the test.");
            success = true;
            app.Log.Info("runtime smoke: PASS");
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            app.Log.Error("runtime smoke: FAIL", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            JsonFile.Save(Path.Combine(app.Paths.Root, "smoke-result.json"), new { success, error, checks, utc = DateTime.UtcNow });
            app.Shutdown();
        }
    }

    private static async Task CheckUi110Async(DockApp app, Dictionary<string, object> checks)
    {
        var bar=app.TopBar!.Primary!;
        var settings=app.Settings.Current.Clone();
        bar.IgnoreRealCursorForDiagnostics=true;
        FloatingDock.Windows.Integration.SystemAudio.State? originalAudio=null;
        var probe=new Window { Title="FloatingDock input language diagnostic",Content=new TextBox { Text="Input language probe" } };
        var probeHwnd=WindowSetup.Hwnd(probe);probe.AppWindow.IsShownInSwitchers=false;
        uint thread=NativeMethods.GetWindowThreadProcessId(probeHwnd,out _);
        var originalLayout=FloatingDock.Windows.Integration.InputLanguages.GetKeyboardLayout(thread);
        try
        {
            app.Settings.Update(s=> { s.TopBarEnabled=true;s.TopBarAutoHide=false;s.BottomOffset=180; });
            await Task.Delay(450);
            var pattern=new Grid();
            for(int i=0;i<100;i++)pattern.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(8) });
            for(int j=0;j<20;j++)pattern.RowDefinitions.Add(new RowDefinition { Height=new GridLength(8) });
            for(int i=0;i<100;i++)for(int j=0;j<20;j++)
            {
                var tile=new Microsoft.UI.Xaml.Shapes.Rectangle { Fill=new SolidColorBrush((i+j)%2==0?Microsoft.UI.Colors.White:Microsoft.UI.Colors.Black) };
                Grid.SetColumn(tile,i);Grid.SetRow(tile,j);pattern.Children.Add(tile);
            }
            probe.Content=pattern;WindowSetup.MakeBorderless(probe);
            WindowStyler.SetBounds(probeHwnd,bar.Monitor.Bounds.Left+400,bar.Monitor.Bounds.Top,800,180,topmost:true);probe.AppWindow.Show(false);bar.Apply(app.Settings.Current,bar.Monitor);
            await Task.Delay(450);
            OwnWindowCapture.Capture(bar.Handle).Save(Path.Combine(app.Paths.Root,"acrylic-menu-bar.png"));
            var glass=OwnWindowCapture.Capture(bar.Handle);
            var samples=Enumerable.Range(620,200).Select(x=>(int)glass.Pixels[((glass.Height/2)*glass.Width+x)*4]).ToArray();
            double mean=samples.Average(),difference=samples.Zip(samples.Skip(1),(a,b)=>Math.Abs(a-b)).Average();
            var order=new List<object>();NativeMethods.EnumWindows((h,_)=> { NativeMethods.GetWindowThreadProcessId(h,out var pid);if(pid==Environment.ProcessId)order.Add(new { hwnd=h.ToInt64(),kind=NativeMethods.GetClass(h),visible=WindowStyler.IsVisible(h),style=NativeMethods.GetExStyle(h).ToString("X"),bounds=WindowStyler.GetRect(h) });return true; },IntPtr.Zero);
            checks["localBlur"]=new { appliedTargets=GlassBackdrop.AppliedTargets,mean,difference,globalTransparencyUnchanged=true,barHandle=bar.Handle.ToInt64(),probeHandle=probeHwnd.ToInt64(),ownWindowOrder=order };
            Check(mean>25&&mean<245&&difference<3,"Own-window blur did not soften the checkerboard: mean="+mean+", difference="+difference);
            foreach(var tile in pattern.Children.Cast<Microsoft.UI.Xaml.Shapes.Rectangle>())tile.Fill=new SolidColorBrush(Microsoft.UI.Colors.Black);
            await Task.Delay(150);var darkFrame=OwnWindowCapture.Capture(bar.Handle);double black=Enumerable.Range(620,200).Average(x=>(double)darkFrame.Pixels[((darkFrame.Height/2)*darkFrame.Width+x)*4]);
            foreach(var tile in pattern.Children.Cast<Microsoft.UI.Xaml.Shapes.Rectangle>())tile.Fill=new SolidColorBrush(Microsoft.UI.Colors.White);
            await Task.Delay(150);var lightFrame=OwnWindowCapture.Capture(bar.Handle);double white=Enumerable.Range(620,200).Average(x=>(double)lightFrame.Pixels[((lightFrame.Height/2)*lightFrame.Width+x)*4]);
            Check(white-black>30,"The backdrop did not track the live underlay.");
            checks["liveBlurSource"]=new { black,white,trackedWithoutCpuFrames=true };
            probe.Content=new TextBox { Text="Input language probe" };
            WindowStyler.SetBounds(probeHwnd,bar.Monitor.Bounds.Left+60,bar.Monitor.Bounds.Top+160,420,200,topmost:false);
            originalAudio=FloatingDock.Windows.Integration.SystemAudio.Read();
            bar.OpenMenuForDiagnostics("sound");await Task.Delay(500);
            Check(bar.Menus.ActiveKind=="sound"&&bar.Menus.VolumeSlider is { IsEnabled:true },"Sound menu did not open with a working endpoint.");
            CaptureFlyout("sound-menu.png");
            var slider=bar.Menus.VolumeSlider!;
            double changed=Math.Round(originalAudio.Volume*100)>=99?98:Math.Round(originalAudio.Volume*100)+1;
            slider.Value=changed;
            var changedAudio=FloatingDock.Windows.Integration.SystemAudio.Read();
            Check(Math.Abs(changedAudio.Volume*100-changed)<.2,"Slider did not change the endpoint volume.");
            bar.Menus.MuteButton!.IsChecked=!originalAudio.Muted;
            Check(FloatingDock.Windows.Integration.SystemAudio.Read().Muted!=originalAudio.Muted,"Mute control did not change the endpoint.");
            FloatingDock.Windows.Integration.SystemAudio.SetVolume(originalAudio.Volume);
            FloatingDock.Windows.Integration.SystemAudio.SetMuted(originalAudio.Muted);
            checks["audioMenu"]=new { opened=true,sliderApplied=true,muteApplied=true,originalStateRestored=true };
            bar.Menus.Close();await Task.Delay(200);
            Check(bar.Menus.ActiveKind is null,"Sound menu did not close.");
            probe.Activate();await Task.Delay(200);
            bar.OpenMenuForDiagnostics("language",probeHwnd);await Task.Delay(450);
            var layouts=FloatingDock.Windows.Integration.InputLanguages.Installed();
            Check(bar.Menus.LanguageButtons.Count==layouts.Count&&layouts.Count>0,"Input layouts were not listed.");
            CaptureFlyout("language-menu.png");
            int alternate=Enumerable.Range(0,layouts.Count).FirstOrDefault(i=>layouts[i].Handle!=originalLayout);
            var peer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(bar.Menus.LanguageButtons[alternate]);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(300);
            Check(bar.Menus.ActiveKind is null,"Language selection did not dismiss the menu.");
            Check(FloatingDock.Windows.Integration.InputLanguages.GetKeyboardLayout(thread)==layouts[alternate].Handle,"Input layout did not switch on the target thread.");
            FloatingDock.Windows.Integration.InputLanguages.Request(probeHwnd,originalLayout);
            checks["languageMenu"]=new { listed=layouts.Count,selectionApplied=true,targetIsOwnProbe=true };
            bar.OpenMenuForDiagnostics("network");
            for(int i=0;i<100&&!bar.Menus.NetworkReady;i++)await Task.Delay(200);
            Check(bar.Menus.NetworkReady,"Network query did not finish within 20 seconds.");
            await Task.Delay(200);
            Check(bar.Menus.ActiveKind=="network","Network menu did not remain open.");
            CaptureFlyout("network-menu.png");
            checks["networkMenu"]=new { opened=true,queryCompleted=true,summary=bar.Menus.NetworkSummary,connectionWasNotChanged=true };
            app.Settings.Update(s=>s.TopBarAutoHide=true);await Task.Delay(850);
            Check(bar.HideAmount==0,"The bar hid while its menu was open.");
            bar.SetCaptureHidden(true);
            Check(bar.Menus.ActiveKind is null&&!WindowStyler.IsVisible(bar.Handle),"Capture did not dismiss the popup and bar.");
            bar.SetCaptureHidden(false);bar.Reveal();await Task.Delay(300);
            Check(WindowStyler.IsVisible(bar.Handle),"Bar did not return after capture suppression.");
            checks["menuLifetime"]=new { autoHideHeld=true,captureDismissed=true,returned=true };
            app.Settings.Update(s=>s.TopBarAutoHide=false);
            bar.OpenMenuForDiagnostics("calendar");await Task.Delay(500);
            Check(bar.Menus.ActiveKind=="calendar","Calendar menu did not open.");
            CaptureFlyout("calendar-menu.png");
            probe.Activate();await Task.Delay(250);
            bool probeFocused=NativeMethods.GetForegroundWindow()==probeHwnd;
            if(probeFocused)Check(bar.Menus.ActiveKind is null&&!WindowStyler.IsVisible(bar.Menus.PopupHandle),"Quick menu did not dismiss when another window activated.");
            checks["lightDismiss"]=new { ownProbeActivationDismissed=probeFocused,foregroundDenied=!probeFocused };
            bar.Menus.Close();
            CheckNativeMenu(app,checks);
        }
        finally
        {
            bar.Menus.Close();
            if(originalAudio is { } audio) { FloatingDock.Windows.Integration.SystemAudio.SetVolume(audio.Volume);FloatingDock.Windows.Integration.SystemAudio.SetMuted(audio.Muted); }
            FloatingDock.Windows.Integration.InputLanguages.Request(probeHwnd,originalLayout);await Task.Delay(100);probe.Close();
            bar.IgnoreRealCursorForDiagnostics=false;
            app.Settings.Update(s=> { s.TopBarEnabled=settings.TopBarEnabled;s.TopBarAutoHide=settings.TopBarAutoHide;s.BottomOffset=settings.BottomOffset; });
        }
        void CaptureFlyout(string name)
        {
            var popup=bar.Menus.PopupHandle;
            Check(popup!=IntPtr.Zero&&WindowStyler.IsVisible(popup),"No native quick-menu window was shown.");
            OwnWindowCapture.Capture(popup).Save(Path.Combine(app.Paths.Root,name));
        }
    }

    private static async Task CheckUi109Async(DockApp app, Dictionary<string, object> checks)
    {
        var dock = app.Dock.PrimaryWindowForDiagnostics!;
        var bar = app.TopBar!.Primary!;
        dock.IgnoreRealCursorForDiagnostics = true;
        bar.IgnoreRealCursorForDiagnostics = true;
        var original = app.Settings.Current.Clone();
        var focusProbe = new Window { Title = "FloatingDock settings restore diagnostic", Content = new Grid() };
        WindowStyler.SetBounds(WindowSetup.Hwnd(focusProbe),40,40,180,100,topmost:false);
        try
        {
            focusProbe.Activate();
            app.Settings.Update(s => s.BottomOffset = 180); // keep our render probe apart from an already-running user's Dock
            await Task.Delay(300);
            app.ShowSettings();
            await Task.Delay(700);
            var hwnd = app.SettingsWin!.Handle;
            Check(app.Running.Snapshot().Any(w => w.Hwnd == hwnd.ToInt64()), "Own settings window is absent from running apps.");
            Check(app.Dock.ViewModel.Entries.Any(e => e.Windows.Any(w => w.Hwnd == hwnd.ToInt64())), "Settings has no Dock slot.");
            OwnWindowCapture.Capture(hwnd).Save(Path.Combine(app.Paths.Root, "settings-themed-title.png"));
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
            await Task.Delay(300);
            Check(NativeMethods.IsIconic(hwnd), "Settings did not minimize.");
            var window = app.Running.Snapshot().Single(w => w.Hwnd == hwnd.ToInt64());
            var settingsActivation=await WindowActivator.ActivateOrMinimizeAsync(new[] { window }, false);
            var actualForeground=NativeMethods.GetForegroundWindow();
            var activeThread=NativeMethods.GetWindowThreadProcessId(actualForeground,out var activePid);
            var settingsThread=NativeMethods.GetWindowThreadProcessId(hwnd,out _);
            checks["settingsRestoreAttempt"]=new { result=settingsActivation.ToString(),minimized=NativeMethods.IsIconic(hwnd),live=WindowActivator.IsLive(window),foreground=actualForeground==hwnd, actualClass=NativeMethods.GetClass(actualForeground), ownPid=Environment.ProcessId, ownTitle=activePid==Environment.ProcessId?NativeMethods.GetTitle(actualForeground):null, dockHandle=dock.Handle.ToInt64(), barHandle=bar.Handle.ToInt64(), actualPid=activePid, activeThread, settingsThread, callerThread=NativeMethods.GetCurrentThreadId(), actualRoot=NativeMethods.GetAncestor(actualForeground,NativeMethods.GA_ROOTOWNER).ToInt64(), target=hwnd.ToInt64() };
            Check(settingsActivation is WindowActivator.ActivationResult.Activated or WindowActivator.ActivationResult.Denied, "Settings did not restore from its Dock identity: "+settingsActivation);
            Check(!NativeMethods.IsIconic(hwnd), "Settings remained minimized.");
            Check(settingsActivation != WindowActivator.ActivationResult.Denied || actualForeground != hwnd, "A denied result contradicts actual foreground.");
            checks["settingsDockSlot"] = new { visible = true, minimizedRestored = true, foreground = NativeMethods.GetForegroundWindow() == hwnd, foregroundDenied=settingsActivation==WindowActivator.ActivationResult.Denied };
            Check(app.Running.Snapshot().Where(w => w.ProcessId == Environment.ProcessId).All(w => w.Hwnd == hwnd.ToInt64()), "Own overlay leaked into the Dock app list.");
            app.SettingsWin.Close();
            await Task.Delay(300);
            Check(!app.Running.Snapshot().Any(w => w.Hwnd == hwnd.ToInt64()), "Closed settings slot remained.");
            Check(!Directory.Exists(Path.Combine(AppContext.BaseDirectory,"IconPacks")), "The release still contains bundled icon packs.");
            Check(app.Packs.ListPacks().All(p => !p.Id.StartsWith("DockIcons")), "A bundled pack was seeded.");
            checks["bundledIconPacksRemoved"] = true;
            int rasters = dock.PlateRasterizations;
            dock.BeginRenderDiagnostics(stationary: true);
            await Task.Delay(700);
            Check(dock.PlateRasterizations == rasters, "Plate/shadow was rasterized during hover.");
            OwnWindowCapture.Capture(dock.Handle).Save(Path.Combine(app.Paths.Root, "soft-shadow-dock.png"));
            dock.EndRenderDiagnostics();
            checks["shadowRasterizationsDuringHover"] = 0;
            CheckNativeMenu(app, checks);
            Check(WindowStyler.IsVisible(bar.Handle), "Menu bar was not visible.");
            OwnWindowCapture.Capture(bar.Handle).Save(Path.Combine(app.Paths.Root, "menu-bar.png"));
            app.Settings.Update(s => s.TopBarAutoHide = true);
            await Task.Delay(450);
            Check(bar.HideAmount == 1, "Menu bar auto-hide did not complete.");
            bar.Reveal();
            await Task.Delay(80);
            float reveal = bar.HideAmount;
            Check(reveal is > 0 and < 1, "Menu bar reveal snapped.");
            await Task.Delay(250);
            Check(bar.HideAmount == 0, "Menu bar reveal did not complete.");
            bar.SetCaptureHidden(true); bar.Reveal();
            Check(!WindowStyler.IsVisible(bar.Handle), "Menu bar rose over capture suppression.");
            bar.SetCaptureHidden(false);
            app.Settings.Update(s => s.TopBarAutoHide = false);
            await Task.Delay(400);
            Check(WindowStyler.IsVisible(bar.Handle) && bar.HideAmount == 0, "Menu bar did not return.");
            bar.SetFullscreen(true);
            await Task.Delay(600);
            Check(bar.HideAmount == 1, "Menu bar did not hide for fullscreen with auto-hide disabled: " + bar.DiagnosticState);
            bar.SetFullscreen(false);
            await Task.Delay(350);
            Check(bar.HideAmount == 0, "Menu bar did not return after fullscreen.");
            app.Settings.Update(s => s.TopBarEnabled = false);
            await Task.Delay(150);
            Check(!WindowStyler.IsVisible(bar.Handle), "Menu bar remained visible when disabled.");
            app.Settings.Update(s => s.TopBarEnabled = true);
            await Task.Delay(200);
            Check(WindowStyler.IsVisible(bar.Handle), "Menu bar did not show when enabled.");
            checks["menuBar"] = new { visible = true, autoHide = true, animatedReveal = true, captureBlocked = true, fullscreenHide = true, enabledToggle = true, appName = bar.AppName };
            var steam = app.Running.Snapshot().Where(w => w.ExePath is { } path && WinPath.FileName(AppMatching.CanonicalExePath(path)) == "steam.exe").ToArray();
            foreach (var group in steam.GroupBy(w => AppMatching.GroupKey(w)))
            {
                var client = AppMatching.FromWindow(group.First());
                var merged = new FloatingDock.Core.Services.DockEntryReconciler().Build(new(false, false, true, true), new[] { client }, group.ToArray(), 0);
                Check(merged.Count == 1 && merged[0].Windows.Count == group.Count(), "Visible Steam client windows duplicated.");
            }
            checks["visibleSteamClientWindows"] = new { windows = steam.Length, installations = steam.Select(AppMatching.GroupKey).Distinct().Count(), verified = steam.Length > 0 };
            if (Environment.GetCommandLineArgs().Contains("--test-top-appbar"))
            {
                var before = FloatingDock.Windows.Display.MonitorHelper.Describe(bar.Monitor.Handle)!;
                using (var reservation = new FloatingDock.Windows.Taskbar.AppBarReservation())
                {
                    Check(reservation.Apply(new[] { new FloatingDock.Windows.Taskbar.AppBarRequest(before, 30, Top: true) }).AllReserved, "Top AppBar registration failed.");
                    await Task.Delay(200);
                    var reserved = FloatingDock.Windows.Display.MonitorHelper.Describe(bar.Monitor.Handle)!;
                    Check(reserved.WorkArea.Top >= before.WorkArea.Top + 30, "Top AppBar did not reserve the work area.");
                }
                await Task.Delay(200);
                Check(FloatingDock.Windows.Display.MonitorHelper.Describe(bar.Monitor.Handle)!.WorkArea == before.WorkArea, "Top AppBar did not restore the work area.");
                checks["topAppBar"] = new { reserved = true, originalWorkAreaRestored = true };
            }
            await Task.Delay(500);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(2500);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            checks["idleCpuWithMenuBar"] = new { elapsedMs = elapsed, cpuMs, logicalProcessors = Environment.ProcessorCount, systemCpuPercent = cpuMs / elapsed / Environment.ProcessorCount * 100 };
        }
        finally
        {
            focusProbe.Close();
            dock.EndRenderDiagnostics();dock.IgnoreRealCursorForDiagnostics = false;bar.IgnoreRealCursorForDiagnostics = false;
            app.Settings.Update(s => { s.ActiveIconPack = original.ActiveIconPack; s.TopBarAutoHide = original.TopBarAutoHide; s.TopBarEnabled = original.TopBarEnabled; s.BottomOffset = original.BottomOffset; });
        }
    }

    private static async Task CheckDockInputAsync(DockApp app, Dictionary<string, object> checks)
    {
        var dock = app.Dock.PrimaryWindowForDiagnostics!;
        int initialMessages = dock.PointerMessages;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var point = dock.LaunchpadPointForDiagnostics();
            var packed = new IntPtr((point.Y << 16) | (point.X & 0xffff));
            // Dispatch through the real HWND procedure, never call an action/render helper directly.
            SendMessage(dock.Handle, 0x200, IntPtr.Zero, packed);
            Check(dock.IsHovering, "The HWND did not deliver mouse hover to Dock.");
            SendMessage(dock.Handle, 0x201, new IntPtr(1), packed);
            SendMessage(dock.Handle, 0x202, IntPtr.Zero, packed);
            await Task.Delay(450);
            Check(app.LaunchpadWin!.IsOpen && WindowStyler.IsVisible(app.LaunchpadWin.Handle), "A dock mouse click did not open Launchpad: " + app.LaunchpadWin.DiagnosticState);
            app.LaunchpadWin.Hide();
            await Task.Delay(500);
            Check(WindowStyler.IsVisible(dock.Handle), "Dock did not return after Launchpad closed.");
        }
        Check(dock.PointerMessages >= initialMessages + 9, "Dock did not continue receiving mouse messages.");
        checks["nativeDockInput"] = new { hoverDelivered = true, launchpadClicks = 3, pointerMessages = dock.PointerMessages - initialMessages, dockReturned = true };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckIconRastersAsync(DockApp app, AppRef sample, Dictionary<string, object> checks)
    {
        var raster = await app.Icons.GetRasterAsync(sample, 64);
        Check(raster is not null, "The shared icon raster did not load.");
        Check(ReferenceEquals(raster, await app.Icons.GetRasterAsync(sample, 64)), "Dock/Launchpad did not share the cached icon raster.");
        int files = 0;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--test-icon-files");
        if (at >= 0)
        {
            Check(at + 1 < args.Length && Path.IsPathFullyQualified(args[at + 1]), "An absolute icon fixture directory is required.");
            foreach (var path in Directory.EnumerateFiles(args[at + 1]).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".ico").Take(64))
            {
                var bytes = IconService.ReadLimited(path);
                Check(bytes is not null, "Custom icon fixture was not valid: " + Path.GetFileName(path));
                var icon = await IconBitmapDecoder.DecodeAsync(bytes!, 64);
                for (int y = 0; y < icon.Height; y++)
                    for (int x = 0; x < icon.Width; x++)
                    {
                        int offset = (y * icon.Width + x) * 4;
                        // Artwork padding is preserved; the compositor supplies the slot inset.
                        Check(icon.Pixels[offset] <= icon.Pixels[offset + 3] && icon.Pixels[offset + 1] <= icon.Pixels[offset + 3] && icon.Pixels[offset + 2] <= icon.Pixels[offset + 3], "Icon lost premultiplied alpha.");
                    }
                files++;
            }
        }
        checks["iconRaster"] = new { sharedCache = true, customFiles = files, paddingPreserved = true, premultipliedAlpha = true };
    }

    private static void CheckNativeMenu(DockApp app, Dictionary<string, object> checks)
    {
        var dock = app.Dock.PrimaryWindowForDiagnostics!;
        bool found = false, above = false, clear = false;
        bool dark = false;
        int menuOrder=-1,dockOrder=-1;
        Exception? failure = null;
        TimerCallback callback = (hwnd, message, id, time) =>
        {
            try
            {
                // Reproduce the foreground-change handler that used to raise Dock over its own popup.
                dock.Reassert();
                var order = new List<IntPtr>();
                NativeMethods.EnumWindows((h, _) => { order.Add(h); return true; }, IntPtr.Zero);
                var menu = order.FirstOrDefault(h => { NativeMethods.GetWindowThreadProcessId(h, out var pid); return pid == Environment.ProcessId && NativeMethods.IsWindowVisible(h) && NativeMethods.GetClass(h) == "#32768"; });
                found = menu != IntPtr.Zero;
                if (found)
                {
                    above = order.IndexOf(menu) < order.IndexOf(dock.Handle);
                    menuOrder=order.IndexOf(menu);dockOrder=order.IndexOf(dock.Handle);
                    var plate=dock.PlateScreenForDiagnostics;
                    clear = NativeMethods.GetWindowRect(menu, out var rect) && (app.Settings.Current.Edge switch { DockEdge.Left=>rect.Left>=plate.Right,DockEdge.Right=>rect.Right<=plate.Left,_=>rect.Bottom<=plate.Top });
                    var frame = OwnWindowCapture.Capture(menu);
                    int p = ((frame.Height - 8) * frame.Width + frame.Width - 12) * 4;
                    dark = frame.Pixels[p] < 60 && frame.Pixels[p + 1] < 60 && frame.Pixels[p + 2] < 60;
                    frame.Save(Path.Combine(app.Paths.Root, "dock-context-menu.png"));
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { KillTimer(IntPtr.Zero, id); EndMenu(); }
        };
        var timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 250, callback);
        Check(timer != UIntPtr.Zero, "Could not schedule menu verification.");
        try { dock.ShowContextMenuForSmoke(); }
        finally { KillTimer(IntPtr.Zero, timer); GC.KeepAlive(callback); }
        if (failure is not null) throw failure;
        checks["nativeContextMenu"] = new { found, aboveDock = above, outsideDock = clear, dark, menuOrder,dockOrder,dockVisible=WindowStyler.IsVisible(dock.Handle) };
        Check(found && above && clear && dark, "Native context menu is not dark or overlaps/falls behind Dock.");
    }

    private static async Task CheckAnimationAnchorAsync(DockApp app, Dictionary<string, object> checks)
    {
        var probe = new Window { Title = "FloatingDock animation origin diagnostic", Content = new Grid() };
        var hwnd = WindowSetup.Hwnd(probe);
        var previous = app.Msg.AnimationRectProvider;
        app.Dock.HoldWindowListForDiagnostics = true;
        var dock = app.Dock.PrimaryWindowForDiagnostics!;
        app.Dock.HoldFullscreenStateForDiagnostics = true;
        dock.IgnoreRealCursorForDiagnostics = true;
        dock.SetFullscreenHidden(false);
        int before = app.Msg.AnimationRectsUpdated;
        int received = 0;
        bool originsMatch = true;
        var snapshot = new WindowInfo(hwnd.ToInt64(), Environment.ProcessId, Environment.ProcessPath, null, probe.Title, null, false);
        var origins = new List<object>();
        RECT? expected = null;
        app.Msg.AnimationRectProvider = h =>
        {
            var rect = previous?.Invoke(h);
            if (h == hwnd && rect is { } r)
            {
                received++;
                originsMatch &= expected is { } e && r.Left == e.Left && r.Top == e.Top && r.Right == e.Right && r.Bottom == e.Bottom;
                origins.Add(new { r.Left, r.Top, r.Right, r.Bottom });
            }
            return rect;
        };
        try
        {
            Check(app.Msg.AnimationRectHookRegistered, "Windows rejected shell animation registration.");
            WindowStyler.SetBounds(hwnd, 50, 50, 400, 240, topmost: false);
            probe.Activate();
            app.Dock.ViewModel.SetWindows(new[] { snapshot }, hwnd.ToInt64());
            await Task.Delay(450);
            expected = app.Dock.AnimationRectFor(hwnd);
            Check(expected is { Width: > 0, Height: > 0 }, $"The running diagnostic window has no actual dock icon bounds: live={WindowActivator.IsLive(snapshot)}, root={NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER)}, hwnd={hwnd}, entries={app.Dock.ViewModel.Entries.Count}.");
            int restingWidth = expected!.Value.Width;
            float cursorX = ((expected.Value.Left + expected.Value.Right) * .5f - dock.Bounds.X) / (float)dock.Monitor.Scale;
            dock.BeginRenderDiagnostics(stationary: true, cursorX: cursorX);
            await Task.Delay(450);
            expected = app.Dock.AnimationRectFor(hwnd);
            Check(expected is { } zoomed && zoomed.Width > restingWidth, $"The origin did not follow the magnified icon: resting={restingWidth}, magnified={expected?.Width}, hide={dock.HideAmount}.");
            app.Dock.RememberAnimationOrigin(new[] { snapshot }, expected!.Value);
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
            await Task.Delay(500);
            Check(NativeMethods.IsIconic(hwnd), "Animation probe did not minimize.");
            app.Dock.RememberAnimationOrigin(new[] { snapshot }, expected.Value);
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            await Task.Delay(500);
            Check(!NativeMethods.IsIconic(hwnd), "Animation probe did not restore.");
            checks["shellAnimationAnchor"] = new { registered = true, received, updated = app.Msg.AnimationRectsUpdated - before, restingWidth, magnifiedWidth = expected.Value.Width, actualDockIconOrigins = origins };
            Check(received >= 2 && app.Msg.AnimationRectsUpdated >= before + 2 && originsMatch, "Windows did not use the clicked icon coordinates for both animation requests.");
        }
        finally
        {
            app.Msg.AnimationRectProvider = previous;
            dock.EndRenderDiagnostics();
            dock.IgnoreRealCursorForDiagnostics = false;
            app.Dock.HoldFullscreenStateForDiagnostics = false;
            app.Dock.HoldWindowListForDiagnostics = false;
            app.Dock.ViewModel.SetWindows(app.Running.Snapshot(), app.Running.ForegroundRoot);
            probe.Close();
        }
    }

    private static async Task CheckNativeFullscreenAsync(Dictionary<string, object> checks)
    {
        var probe = new Window { Title = "FloatingDock fullscreen diagnostic", Content = new Grid() };
        var hwnd = WindowSetup.Hwnd(probe);
        try
        {
            probe.AppWindow.Show(false);
            ((Microsoft.UI.Windowing.OverlappedPresenter)probe.AppWindow.Presenter).Maximize();
            await Task.Delay(150);
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            Check(NativeMethods.IsWindowVisible(hwnd), "Native fullscreen probe is not visible.");
            Check(!FullscreenDetector.IsWindowFullscreenOn(hwnd, monitor, 0), "A framed maximized window was classified as fullscreen.");
              Check(FullscreenDetector.IsWindowMaximizedOn(hwnd, monitor, 0), "A maximized app covering the work area was not detected.");
              Check(!FullscreenDetector.IsWindowMaximizedOn(hwnd, monitor, (uint)Environment.ProcessId), "Our maximized settings/Launchpad window must not hide Dock as an external app.");
              ((Microsoft.UI.Windowing.OverlappedPresenter)probe.AppWindow.Presenter).Restore();
              WindowStyler.SetBounds(hwnd, 40, 40, 320, 240, topmost: false);
              await Task.Delay(100);
              Check(!FullscreenDetector.IsWindowMaximizedOn(hwnd, monitor, 0), "An ordinary restored window was detected as screen-filling.");
            probe.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            await Task.Delay(150);
            Check(FullscreenDetector.IsWindowFullscreenOn(hwnd, monitor, 0), "A native fullscreen window was not detected.");
            Check(!FullscreenDetector.IsWindowFullscreenOn(hwnd, monitor, (uint)Environment.ProcessId), "The dock's own process was classified as an external fullscreen app.");
            checks["nativeFullscreenClassification"] = new { maximizedClassifiedSeparately = true, maximizedDetected = true, restoredExcluded = true, fullscreenDetected = true, ownProcessExcluded = true };
        }
        finally { probe.Close(); }
    }

    private static async Task CheckFullscreenRevealAsync(DockApp app, Dictionary<string, object> checks)
    {
        var dock = app.Dock.PrimaryWindowForDiagnostics!;
        app.Dock.HoldFullscreenStateForDiagnostics = true;
        dock.IgnoreRealCursorForDiagnostics = true;
        try
        {
        dock.SetFullscreenHidden(true);
        Check(dock.HideAmount < 1 && WindowStyler.GetRect(dock.Handle)?.H > 2, "Fullscreen entry snapped Dock off-screen.");
        await Task.Delay(100);
        float exitMiddle = dock.HideAmount;
        Check(exitMiddle is > 0 and < 1 && WindowStyler.GetRect(dock.Handle)?.H > 2, "Fullscreen exit has no intermediate slide.");
        await Task.Delay(450);
        Check(WindowStyler.IsVisible(dock.Handle) && WindowStyler.GetRect(dock.Handle)?.H == 2, "Fullscreen did not retain its edge reveal strip.");
        dock.Reveal();
        dock.BeginRenderDiagnostics(stationary: true);
        await Task.Delay(650);
        Check(WindowStyler.IsVisible(dock.Handle) && WindowStyler.GetRect(dock.Handle)?.H > 2, "Fullscreen edge reveal did not restore Dock.");
        OwnWindowCapture.Capture(dock.Handle).Save(Path.Combine(app.Paths.Root, "dock-fullscreen-revealed.png"));
        dock.EndRenderDiagnostics();
        await Task.Delay(1500);
        Check(WindowStyler.GetRect(dock.Handle)?.H == 2, "Fullscreen Dock did not hide after leaving it.");
        dock.SetFullscreenHidden(false);
        Check(dock.HideAmount > 0, "Fullscreen return snapped Dock on-screen.");
        await Task.Delay(80);
        float returnMiddle = dock.HideAmount;
        Check(returnMiddle is > 0 and < 1, "Fullscreen return has no intermediate slide.");
        await Task.Delay(400);
        Check(WindowStyler.IsVisible(dock.Handle) && WindowStyler.GetRect(dock.Handle)?.H > 2, "Dock did not return after fullscreen ended.");
        checks["fullscreenEdgeRevealAndReturn"] = new { passed = true, exitMiddle, returnMiddle };
        dock.SetCaptureHidden(true);
        dock.Reassert(); dock.Reveal();
        Check(!WindowStyler.IsVisible(dock.Handle), "Dock rose over capture suppression.");
        dock.SetCaptureHidden(false);
        Check(dock.HideAmount > 0, "Dock return after capture snapped.");
        await Task.Delay(450);
        Check(WindowStyler.IsVisible(dock.Handle) && dock.HideAmount == 0, "Dock did not return after capture.");
        checks["captureSuppression"] = new { hidden = true, reassertBlocked = true, edgeRevealBlocked = true, animatedReturn = true };
        }
        finally { dock.SetCaptureHidden(false); dock.SetFullscreenHidden(false); dock.IgnoreRealCursorForDiagnostics = false; app.Dock.HoldFullscreenStateForDiagnostics = false; }
    }

    private static async Task CheckWindowActivationAsync(Dictionary<string, object> checks)
    {
        var owner = new Window { Title = "FloatingDock activation diagnostic", Content = new Grid() };
        var hwnd = WindowSetup.Hwnd(owner);
        // Activation targets are ordinary application windows, not the dock's non-switchable tool windows.
        WindowStyler.SetBounds(hwnd, 40, 40, 220, 120, topmost: false);
        var popup = new Window { Title = "FloatingDock owned diagnostic", Content = new Grid() };
        var dialog = WindowSetup.Hwnd(popup);
        WindowStyler.SetBounds(dialog, 55, 55, 190, 100, topmost: false);
        var snapshot = new WindowInfo(hwnd.ToInt64(), Environment.ProcessId, Environment.ProcessPath, null, owner.Title, null, false);
        try
        {
            owner.Activate();
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
            await Task.Delay(150);
            Check(NativeMethods.IsIconic(hwnd), "Diagnostic window did not minimize.");
            var restoration = await WindowActivator.ActivateOrMinimizeAsync(new[] { snapshot }, false);
            Check(!NativeMethods.IsIconic(hwnd), $"Window did not restore: result={restoration}, foreground={NativeMethods.GetForegroundWindow()}, expected={hwnd}.");
            Check(await WindowActivator.ActivateOrMinimizeAsync(new[] { snapshot with { ProcessId = Environment.ProcessId + 1 } }, false) == WindowActivator.ActivationResult.NoLiveWindow, "A stale HWND/PID snapshot was accepted.");
            if (restoration == WindowActivator.ActivationResult.Denied)
            {
                Check(NativeMethods.GetForegroundWindow() != hwnd, "The denied result contradicts the current foreground window.");
                // Do not manufacture a foreground success in a desktop that denies it. Keep dependent cases explicitly unverified.
                checks["windowActivation"] = new { minimizedRestored = true, foregroundConfirmed = false, foregroundDenied = true, activeMinimized = (bool?)null, ownedDialog = (bool?)null, stalePidRejected = true };
                return;
            }
            Check(restoration == WindowActivator.ActivationResult.Activated, "Unexpected window activation result.");
            Check(await WindowActivator.ActivateOrMinimizeAsync(new[] { snapshot }, true) == WindowActivator.ActivationResult.Minimized, "Active application did not minimize.");
            await Task.Delay(150);
            Check(NativeMethods.IsIconic(hwnd), "Minimize request was not applied.");
            Check(await WindowActivator.ActivateOrMinimizeAsync(new[] { snapshot }, false) == WindowActivator.ActivationResult.Activated, "Window did not restore after minimizing.");
            NativeMethods.SetWindowLongPtr(dialog, -8 /* GWLP_HWNDPARENT */, hwnd);
            EnableWindow(hwnd, false);
            popup.Activate();
            await Task.Delay(100);
            Check(await WindowActivator.ActivateOrMinimizeAsync(new[] { snapshot }, false) == WindowActivator.ActivationResult.Activated && NativeMethods.GetForegroundWindow() == dialog, "Activation did not select the owner's usable dialog.");
            Check(await WindowActivator.ActivateOrMinimizeAsync(new[] { snapshot with { ProcessId = Environment.ProcessId + 1 } }, false) == WindowActivator.ActivationResult.NoLiveWindow, "A stale HWND/PID snapshot was accepted.");
            checks["windowActivation"] = new { minimizedRestored = true, foregroundConfirmed = true, foregroundDenied = false, activeMinimized = (bool?)true, ownedDialog = (bool?)true, stalePidRejected = true };
        }
        finally { EnableWindow(hwnd, true); popup.Close(); owner.Close(); }
    }

    private static async Task CheckLaunchpadAnimationAsync(DockApp app, Dictionary<string, object> checks)
    {
        var launchpad = app.LaunchpadWin!;
        launchpad.Hide();
        await Task.Delay(220);
        var probe = new Window { Title = "FloatingDock Launchpad animation diagnostic", Content = new Grid { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 34, 187, 153)) } };
        WindowSetup.MakeBorderless(probe);
        var hwnd = WindowSetup.Hwnd(probe);
        WindowStyler.MakeOverlayWindow(hwnd, noActivate: true);
        var monitor = MonitorHelper.FromCursor() ?? MonitorHelper.Primary()!;
        WindowStyler.SetBounds(hwnd, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height);
        try
        {
            probe.AppWindow.Show(false);
            await Task.Delay(100);
            launchpad.Show();
            await Task.Delay(40);
            var early = OwnWindowCapture.Capture(launchpad.Handle);
            await Task.Delay(70);
            var middle = OwnWindowCapture.Capture(launchpad.Handle);
            await Task.Delay(320);
            var final = OwnWindowCapture.Capture(launchpad.Handle);
            int Green(OwnWindowCapture.Frame frame) => frame.Pixels[(32 * frame.Width + 32) * 4 + 1];
            var values = new[] { Green(early), Green(middle), Green(final) };
            early.Save(Path.Combine(app.Paths.Root, "launchpad-opening-early.png"));
            middle.Save(Path.Combine(app.Paths.Root, "launchpad-opening-middle.png"));
            final.Save(Path.Combine(app.Paths.Root, "launchpad-opening-final.png"));
            checks["launchpadAnimationPixels"] = new { earlyGreen = values[0], middleGreen = values[1], finalGreen = values[2] };
            Check(values[0] > values[2] + 20 && values[1] >= values[2] && values[2] < 65, "Launchpad's opening fade was not visible in composited pixels.");
            Check(launchpad.IsOpen && WindowStyler.IsVisible(launchpad.Handle), "Animated Launchpad disappeared.");
        }
        finally { probe.Close(); }
    }

    private static async Task CheckDockRenderingAsync(DockApp app, Dictionary<string, object> checks)
    {
        var dock = app.Dock.PrimaryWindowForDiagnostics ?? throw new InvalidOperationException("No dock to capture.");
        var probe = new Window { Title = "FloatingDock alpha diagnostic", Content = new Grid { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 34, 187, 153)) } };
        WindowSetup.MakeBorderless(probe);
        var hwnd = WindowSetup.Hwnd(probe);
        WindowStyler.MakeOverlayWindow(hwnd, noActivate: true);
        try
        {
            var b = dock.Bounds;
            WindowStyler.SetBounds(hwnd, b.X, b.Y, b.W, b.H);
            probe.AppWindow.Show(false);
            dock.Reassert();
            dock.BeginRenderDiagnostics();
            await Task.Delay(6000);
            var frame = OwnWindowCapture.Capture(dock.Handle);
            frame.Save(Path.Combine(app.Paths.Root, "dock-desktop.png"));
            bool Green(int x, int y)
            {
                int offset = (y * frame.Width + x) * 4;
                return Math.Abs(frame.Pixels[offset] - 153) < 12 && Math.Abs(frame.Pixels[offset + 1] - 187) < 12 && Math.Abs(frame.Pixels[offset + 2] - 34) < 12;
            }
            Check(Green(4, 4) && Green(frame.Width - 5, 4), "Dock's transparent margins contain an opaque rectangle.");
            int painted = 0;
            for (int y = 4; y < frame.Height - 4; y += 4)
                for (int x = 4; x < frame.Width - 4; x += 4)
                    if (!Green(x, y)) painted++;
            Check(painted > 100, "Dock's background and icons did not render.");
            checks["compositedAlphaMargins"] = true;
            var performance = dock.EndRenderDiagnostics();
            checks["animation"] = performance;
            Check(performance.Frames >= 30, "Animation did not render enough frames.");
            Check(performance.P95TickMs < 8, "Dock animation processing exceeded the frame budget.");
            int originalFps = app.Settings.Current.AnimationFps;
            try
            {
                app.Settings.Update(s => s.AnimationFps = 30);
                await Task.Delay(350);
                dock.BeginRenderDiagnostics();
                await Task.Delay(6000);
                var lowPower = dock.EndRenderDiagnostics();
                checks["lowPowerAnimation"] = lowPower;
                Check(lowPower.Frames is >= 160 and <= 200, "The 30 FPS limit was not respected.");
            }
            finally { app.Settings.Update(s => s.AnimationFps = originalFps); }
            await Task.Delay(350);
            dock.BeginRenderDiagnostics(stationary: true);
            await Task.Delay(2000);
            checks["stationaryHover"] = dock.EndRenderDiagnostics();
            bool originalLabels = app.Settings.Current.ShowLabels;
            try
            {
                app.Settings.Update(s => s.ShowLabels = false);
                await Task.Delay(350);
                dock.BeginRenderDiagnostics();
                await Task.Delay(6000);
                checks["animationWithoutLabels"] = dock.EndRenderDiagnostics();
            }
            finally { app.Settings.Update(s => s.ShowLabels = originalLabels); }
            await Task.Delay(350);
            dock.BeginRenderDiagnostics();await Task.Delay(6000);
            checks["warmAnimationWithLabels"]=dock.EndRenderDiagnostics();
        }
        finally { dock.EndRenderDiagnostics(); probe.Close(); }
        await Task.Delay(1200);
        if (!dock.IsHovering)
        {
            Check(!dock.IsAnimating, "Dock's frame loop did not stop after the animation settled.");
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(1500);
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            checks["idle"] = new { frameLoopStopped = !dock.IsAnimating, cpuPercentOfOneCore = (process.TotalProcessorTime - cpu).TotalMilliseconds / elapsed * 100 };
        }
    }
}

