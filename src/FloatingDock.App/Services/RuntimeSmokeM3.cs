using FloatingDock.Core.Configuration;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static T? Find<T>(Microsoft.UI.Xaml.DependencyObject root) where T:class
    {
        if(root is T hit)return hit;
        int count=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for(int i=0;i<count;i++){var found=Find<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root,i));if(found is not null)return found;}
        return null;
    }
    /// <summary>Opens every settings page, the search palette and Launchpad in both themes and saves window captures for review.</summary>
    private static async Task CheckM3Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var shots=new List<string>();
        void Save(string name,IntPtr hwnd)
        {
            var frame=OwnWindowCapture.Capture(hwnd);
            var path=Path.Combine(app.Paths.Root,"m3-"+name+".png");frame.Save(path);shots.Add(path);
        }
        try
        {
            app.Settings.Update(s=>{s.TopBarEnabled=true;s.TopBarSearchEnabled=true;s.MenuAnimations=false;s.TilingEnabled=false;});
            foreach(var theme in new[]{ThemeMode.Dark})
            {
                app.Settings.Update(s=>s.Theme=theme);await Task.Delay(300);
                string t=theme==ThemeMode.Dark?"dark":"light";
                foreach(var page in new[]{"Dock","Appearance","Behaviour","Menu Bar","Search","Icon Packs","About"})
                {
                    app.ShowSettings(page);await Task.Delay(900);
                    Check(app.SettingsWin?.XamlRoot is not null,"Settings did not load: "+page);
                    Save("settings-"+page.Replace(' ','_')+"-"+t,app.SettingsWin!.Handle);
                }
                // The accent swatches sit at the bottom of Appearance.
                app.ShowSettings("Appearance");await Task.Delay(900);
                if(app.SettingsWin?.XamlRoot?.Content is Microsoft.UI.Xaml.DependencyObject appearanceTree)
                {
                    var scrolls=new List<Microsoft.UI.Xaml.Controls.ScrollViewer>();
                    void Collect(Microsoft.UI.Xaml.DependencyObject d){if(d is Microsoft.UI.Xaml.Controls.ScrollViewer s)scrolls.Add(s);int n=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);for(int i=0;i<n;i++)Collect(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d,i));}
                    Collect(appearanceTree);
                    var main=scrolls.LastOrDefault(s=>s.ScrollableHeight>0);
                    if(main is not null){main.ChangeView(null,main.ScrollableHeight,null,true);await Task.Delay(500);Save("settings-Appearance-bottom-"+t,app.SettingsWin.Handle);}
                }
                // Transient surfaces: an open combo box, an expanded "Advanced" group and a dialog.
                app.ShowSettings("Dock");await Task.Delay(900);
                if(app.SettingsWin?.XamlRoot?.Content is Microsoft.UI.Xaml.DependencyObject tree)
                {
                    var combo=Find<Microsoft.UI.Xaml.Controls.ComboBox>(tree);var expander=Find<Microsoft.UI.Xaml.Controls.Expander>(tree);
                    if(expander is not null){expander.IsExpanded=true;await Task.Delay(500);}
                    if(combo is not null){combo.IsDropDownOpen=true;await Task.Delay(500);Save("combo-open-"+t,app.SettingsWin.Handle);combo.IsDropDownOpen=false;await Task.Delay(200);}
                    var dialog=new Microsoft.UI.Xaml.Controls.ContentDialog{Title="Reset settings",Content=new Microsoft.UI.Xaml.Controls.TextBlock{Text="All dock settings return to their defaults.",TextWrapping=Microsoft.UI.Xaml.TextWrapping.Wrap},PrimaryButtonText="Reset",CloseButtonText="Cancel",XamlRoot=app.SettingsWin.XamlRoot,DefaultButton=Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary};
                    FloatingDock.UI.Views.M3Dialog.Style(dialog);
                    var shown=dialog.ShowAsync();await Task.Delay(700);Save("dialog-"+t,app.SettingsWin.Handle);dialog.Hide();await shown;await Task.Delay(200);
                }
                app.SettingsWin!.Close();await Task.Delay(200);
                if(app.TopBar?.Primary is { } bar)
                {
                    bar.IgnoreRealCursorForDiagnostics=true;bar.SetFullscreen(false);bar.Target(false);await Task.Delay(300);
                    Save("bar-"+t,bar.Handle);
                    foreach(var kind in new[]{"main","sound","network","calendar","language","media"})
                    {
                        try{bar.OpenMenuForDiagnostics(kind);await Task.Delay(900);if(bar.Menus.SurfaceVisible)Save("menu-"+kind+"-"+t,bar.Menus.PopupHandle);}
                        catch(Exception ex){app.Log.Warn("m3 capture "+kind+": "+ex.Message);}
                        bar.Menus.Close(immediately:true);await Task.Delay(250);
                    }
                    bar.IgnoreRealCursorForDiagnostics=false;
                }
                var items=new[]{new FloatingDock.Windows.Integration.MenuItemSpec(1,"New window",Glyph:"E8A7"),new FloatingDock.Windows.Integration.MenuItemSpec(2,"Pin to Dock",Glyph:"E718"),new FloatingDock.Windows.Integration.MenuItemSpec(3,"Open file location",Glyph:"E8B7"),FloatingDock.Windows.Integration.MenuItemSpec.Separator,new FloatingDock.Windows.Integration.MenuItemSpec(4,"Change icon…",Glyph:"EB9F"),new FloatingDock.Windows.Integration.MenuItemSpec(5,"Reset icon",Disabled:true,Glyph:"E777"),FloatingDock.Windows.Integration.MenuItemSpec.Separator,new FloatingDock.Windows.Integration.MenuItemSpec(6,"Launchpad",Glyph:"E80A"),new FloatingDock.Windows.Integration.MenuItemSpec(7,"Quit FloatingDock",Glyph:"E7E8")};
                _=FloatingDock.UI.Views.PopMenu.ShowAsync(items,800,600,bottomAlign:false);await Task.Delay(900);
                if(FloatingDock.UI.Views.PopMenu.SharedHandle!=IntPtr.Zero)Save("context-menu-"+t,FloatingDock.UI.Views.PopMenu.SharedHandle);
                app.ToggleSearch();await Task.Delay(230);
                if(app.Palette is not null)Save("search-mid-"+t,app.Palette.Handle);
                await Task.Delay(500);
                if(app.Palette is not null){await app.Palette.SearchForDiagnostics("a");await Task.Delay(600);Save("search-"+t,app.Palette.Handle);app.Palette.Hide();await Task.Delay(300);}
                app.ToggleLaunchpad();await Task.Delay(1200);
                if(app.LaunchpadWin is not null){Save("launchpad-"+t,app.LaunchpadWin.Handle);app.LaunchpadWin.Hide();await Task.Delay(400);}
            }
            checks["m3Captures"]=shots;
        }
        finally
        {
            app.Palette?.Hide();
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
}
