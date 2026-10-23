using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static IEnumerable<DependencyObject> AuditDescendants(DependencyObject root)
    {
        yield return root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in AuditDescendants(VisualTreeHelper.GetChild(root,i)))yield return child;
    }

    private static async Task CheckAudit159Async(DockApp app,Dictionary<string,object> checks)
    {
        // Every store and window in this suite belongs to the empty smoke profile.
        var pin=new AppRef {Name="Audit Explorer alias",ExePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")};
        app.Pinned.ReplaceAll(new[]{pin});
        var icon=Path.Combine(app.Paths.Root,"audit-icon.png");
        File.WriteAllBytes(icon,PngWriter.EncodeBgra(2,2,new byte[16],false));
        var alias=new AppRef {Aumid=AppMatching.ExplorerAumid};
        app.Overrides.Set(alias.Key,icon);
        Check(app.Overrides.Has(pin),"The Explorer alias fixture did not resolve its override.");
        app.ShowSettings("Icon Packs");await Task.Delay(350);
        var row=AuditDescendants(app.SettingsWin!.XamlRoot!.Content).OfType<Grid>()
            .First(g=>g.Children.OfType<TextBlock>().Any(t=>t.Text==pin.Name));
        var reset=row.Children.OfType<Button>().Single(b=>b.Content?.ToString()==L.Text("Reset"));
        Check(reset.IsEnabled,"Settings disabled Reset for an override stored under an equivalent app identity.");
        ((IInvokeProvider)new ButtonAutomationPeer(reset).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        Check(!app.Overrides.Has(pin)&&!app.Overrides.Has(alias),"Settings did not reset every equivalent icon identity.");
        checks["settingsAliasReset"]=true;
        app.Log.Info("audit159: settings alias reset passed");
        app.SettingsWin.Close();await Task.Delay(100);

        JsonFile.Save(app.Paths.SearchUsageFile,new Dictionary<string,object?> {[pin.Key]=null});
        app.ToggleSearch();await app.Palette!.SearchForDiagnostics("");
        Check(app.Palette.ResultCount==0,"An invalid history entry prevented the empty-search state.");
        app.Palette.Hide();checks["invalidSearchHistory"]=true;
        app.Log.Info("audit159: invalid search history passed");

        app.ToggleLaunchpad();await Task.Delay(350);
        var library=app.Paths.AppLibraryFile;var saved=library+".audit-saved";
        bool existed=File.Exists(library);if(existed)File.Move(library,saved);
        Directory.CreateDirectory(library);
        try
        {
            int before=app.UserTiles.Snapshot().Count;
            Check(!await app.LaunchpadWin!.AddApplicationPathAsync(Environment.ProcessPath!),"Launchpad reported success after a failed save.");
            Check(app.UserTiles.Snapshot().Count==before,"A failed import modified the live library.");
            checks["launchpadSaveFailure"]=true;
            app.Log.Info("audit159: Launchpad save failure passed");
        }
        finally {Directory.Delete(library);if(existed)File.Move(saved,library);}
        app.LaunchpadWin!.Hide();await Task.Delay(250);

        for(int i=0;i<3;i++)
        {
            app.Log.Info("audit159: closing settings with pending update, cycle "+i);
            app.ShowSettings("Appearance");await Task.Delay(100);
            var window=app.SettingsWin!;
            app.Settings.Update(s=>s.Language=s.Language==InterfaceLanguage.English?InterfaceLanguage.Russian:InterfaceLanguage.English);
            window.Close(); // The preceding update already queued this window's UI callback.
            await Task.Delay(350);
        }
        Check(Program.UiFailureCount==0,"A queued settings update touched a closed window.");
        checks["settingsCloseWithPendingUpdate"]=true;
    }
}
