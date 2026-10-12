using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private sealed class FakeMedia : IMediaController
    {
        internal readonly List<string> Commands=new();
        public Task<MediaState?> ReadAsync()=>Task.FromResult<MediaState?>(new("Smoke track","FloatingDock","Own fake session",true,true,true,true));
        public Task<bool> ControlAsync(string command){Commands.Add(command);return Task.FromResult(true);}
    }
    private static async Task CheckUi120Async(DockApp app,Dictionary<string,object> checks)
    {
        var dock=app.Dock.PrimaryWindowForDiagnostics!;var bar=app.TopBar!.Primary!;var original=app.Settings.Current.Clone();
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Dock.HoldWindowListForDiagnostics=true;dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        var probe=new Window { Title="FloatingDock repeated preview probe",Content=new Grid { Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,34,187,153)) } };
        WindowSetup.MakeBorderless(probe);var hwnd=WindowSetup.Hwnd(probe);
        var monitor=dock.Monitor;WindowStyler.SetBounds(hwnd,monitor.Bounds.Left+450,monitor.Bounds.Top+220,640,360,topmost:false);probe.Activate();
        var entry=new DockEntry { Key="ui120-preview",Kind=DockEntryKind.App,Name="Own preview",Windows=new[]{new WindowInfo(hwnd.ToInt64(),Environment.ProcessId,Environment.ProcessPath,null,probe.Title,null,false)} };
        var anchor=new RECT { Left=monitor.Bounds.Left+100,Right=monitor.Bounds.Left+146,Top=monitor.Bounds.Top+220,Bottom=monitor.Bounds.Top+266 };
        var preview=dock.PreviewForDiagnostics;
        static void Invoke(Button button)
        {
            var peer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        try
        {
            app.Settings.Update(s=> { s.AutoHide=false;s.Edge=DockEdge.Left;s.ShowWindowPreviews=true;s.PreviewDelayMs=200;s.TopBarAutoHide=false;s.TopBarEnabled=true; });await Task.Delay(350);
            for(int i=0;i<3;i++)
            {
                preview.Track(null,default,diagnostic:true);preview.Track(entry,anchor,diagnostic:true,edge:DockEdge.Left);await Task.Delay(420);
                Check(preview.Visible&&preview.ThumbnailCount==1,"Preview failed to show on repeat "+i);
                Check(dock.LabelOpacity==0,"App tooltip remains above a live preview.");
                preview.DismissForInteraction(entry.Key);Check(!preview.Visible,"Icon interaction did not dismiss the thumbnail.");
                await WindowActivator.ActivateOrMinimizeAsync(entry.Windows,true);await Task.Delay(300);
                preview.Track(entry,anchor,diagnostic:true);await Task.Delay(300);Check(!preview.Visible,"Preview reopened under a repeated icon interaction.");
                await WindowActivator.ActivateOrMinimizeAsync(entry.Windows,false);await Task.Delay(350);
                Check(!NativeMethods.IsIconic(hwnd),"Repeated activation did not restore the own test window.");
            }
            preview.Track(null,default,diagnostic:true);preview.Track(entry,anchor,diagnostic:true,edge:DockEdge.Left);await Task.Delay(450);
            var frame=OwnWindowCapture.Capture(preview.Handle);int center=(frame.Height/2*frame.Width+frame.Width/2)*4;
            Check(frame.Pixels[center+1]>frame.Pixels[center+2]+40,"Repeated preview is blank instead of the source window.");
            frame.Save(Path.Combine(app.Paths.Root,"preview-repeated-restored.png"));preview.Hide();
            checks["repeatedPreview"]=new { cycles=3,sourceVisibleAfterRestore=true,labelHidden=true,clickSuppression=true };
            app.Settings.Update(s=>s.ShowWindowPreviews=false);await Task.Delay(120);
            dock.SetHoverCursorForDiagnostics(true);await Task.Delay(1300);
            Check(dock.IsHovering&&!dock.IsAnimating,"Stationary hover did not reach a settled frame loop.");
            dock.SetHoverCursorForDiagnostics(false);await Task.Delay(700);
            Check(!dock.IsHovering&&!dock.IsAnimating,"Watchdog did not release magnification without a native leave event.");
            dock.ClearHoverCursorForDiagnostics();app.Settings.Update(s=>s.ShowWindowPreviews=true);checks["hoverReleaseSettles"]=new { withoutPointerEvent=true,fromStoppedFrameLoop=true };
            app.ShowSettings("Appearance");await Task.Delay(300);app.Settings.Update(s=>s.Language=InterfaceLanguage.Russian);await Task.Delay(250);
            Check(app.SettingsWin!.NavigationLabels.Contains("Оформление")&&app.SettingsWin.NavigationLabels.Contains("Поиск"),"Russian navigation did not refresh.");
            OwnWindowCapture.Capture(app.SettingsWin.Handle).Save(Path.Combine(app.Paths.Root,"settings-russian.png"));
            app.Settings.Update(s=>s.Language=InterfaceLanguage.English);await Task.Delay(250);Check(app.SettingsWin.NavigationLabels.Contains("Appearance"),"English navigation did not restore.");
            app.ShowSettings("Menu Bar");await Task.Delay(200);OwnWindowCapture.Capture(app.SettingsWin.Handle).Save(Path.Combine(app.Paths.Root,"settings-menu-bar.png"));app.SettingsWin.Close();
            checks["languageRoundTrip"]=true;
            foreach(var style in Enum.GetValues<LaunchpadIconStyle>())
            {
                app.Settings.Update(s=>s.LaunchpadIconStyle=style);await Task.Delay(350);
                OwnWindowCapture.Capture(dock.Handle).Save(Path.Combine(app.Paths.Root,"launchpad-icon-"+style+".png"));
            }
            await app.PrepareLaunchpadAsync();app.LaunchpadWin!.Show();await Task.Delay(400);
            Check(!app.LaunchpadWin.HasSearch&&app.LaunchpadWin.HasAddApplication,"Launchpad still searches or lacks add.");
            Check(await app.LaunchpadWin.AddApplicationPathAsync(Environment.ProcessPath!),"Launchpad failed to import an own application.");await Task.Delay(180);
            Check(app.UserTiles.Snapshot().Any(a=>a.ExePath==Environment.ProcessPath),"Launchpad import did not persist the new tile.");
            OwnWindowCapture.Capture(app.LaunchpadWin.Handle).Save(Path.Combine(app.Paths.Root,"launchpad-add.png"));app.LaunchpadWin.Hide();await Task.Delay(280);checks["launchpadAddAndStyles"]=true;
            var media=new FakeMedia();bar.Menus.MediaSource=media;bar.OpenMenuForDiagnostics("media");await Task.Delay(350);
            Check(bar.Menus.MediaReady&&bar.Menus.MediaButtons.Count==3&&bar.Menus.MediaButtons.All(b=>b.IsEnabled),"Media menu did not expose session controls.");
            foreach(var button in bar.Menus.MediaButtons.ToArray()){Invoke(button);await Task.Delay(60);}
            Check(media.Commands.SequenceEqual(new[]{"previous","toggle","next"}),"Media buttons used incorrect transport commands.");
            OwnWindowCapture.Capture(bar.Menus.PopupHandle).Save(Path.Combine(app.Paths.Root,"media-menu.png"));bar.Menus.Close();await Task.Delay(260);Check(!bar.Menus.SurfaceVisible,"Media polling left a menu visible.");checks["mediaTransportAndDismissal"]=true;
            var nativeTray=await Task.Run(FloatingDock.Windows.Integration.SystemTrayOverflow.Describe);
            if(nativeTray.Any(line=>line.Contains("SystemTray.NormalButton")&&line.Contains("скрытые")))
            {
                probe.Activate();await Task.Delay(150);
                for(int i=0;i<2;i++)
                {
                    await bar.OpenTrayForDiagnostics();await Task.Delay(180);Check(bar.TrayVisible,"The real Explorer tray did not stay open: "+bar.DiagnosticState);
                    bar.SetCaptureHidden(true);Check(!FloatingDock.Windows.Integration.SystemTrayOverflow.Visible,"Capture left real tray pixels visible.");
                    bar.SetCaptureHidden(false);bar.Target(false);await Task.Delay(300);
                }
                checks["realExplorerTray"]=new { repeatedCycles=2,captureDismissesImmediately=true };
            }
            else checks["realExplorerTray"]="Explorer did not expose a supported overflow chevron on this test host.";
            var fixture=Path.Combine(app.Paths.Root,"search-files");Directory.CreateDirectory(fixture);File.WriteAllText(Path.Combine(fixture,"FloatingDock-palette-fixture.txt"),"file contents ignored");
            app.Settings.Update(s=> { s.SearchFiles=true;s.SearchFolders=fixture;s.SearchResultLimit=10; });
            app.ToggleSearch();await app.Palette!.SearchForDiagnostics("FloatingDock-palette-fixture");
            Check(app.Palette.Visible&&app.Palette.ResultCount==1,"Palette did not find the scoped file fixture.");
            await Task.Delay(200);
            OwnWindowCapture.Capture(app.Palette.Handle).Save(Path.Combine(app.Paths.Root,"command-palette.png"));
            var stale=app.Palette.SearchForDiagnostics("unmatched-long-query");app.Palette.Hide();await stale;
            Check(!app.Palette.Visible&&!NativeMethods.IsWindowVisible(app.Palette.Handle),"Cancelled search resurrected the palette.");checks["paletteScopedFileAndCancellation"]=true;
            app.Settings.Update(s=> { s.TopBarAnimationFps=30;s.TopBarHideMs=400;s.TopBarShowMs=300; });await Task.Delay(150);
            bar.Target(true);await Task.Delay(600);Check(bar.HideAmount==1,"Configured top bar hide failed.");bar.Target(false);await Task.Delay(500);Check(bar.HideAmount==0,"Configured top bar show failed.");checks["configurableTopBarMotion"]=true;
        }
        finally
        {
            preview.Hide();app.Palette?.Hide();bar.Menus.Close(immediately:true);app.SettingsWin?.Close();probe.Close();
            dock.ClearHoverCursorForDiagnostics();
            app.Settings.Update(s=> { foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(original)); });
            app.Dock.HoldFullscreenStateForDiagnostics=false;app.Dock.HoldWindowListForDiagnostics=false;dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
        }
    }
}
