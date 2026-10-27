using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Views;

internal static class PowerActions
{
    internal static async Task ConfirmAsync(XamlRoot root,bool restart)
    {
        var dialog=new ContentDialog
        {
            XamlRoot=root,Title=L.Text(restart?"Restart this PC?":"Shut down this PC?"),
            Content=L.Text("Save your work before continuing."),
            PrimaryButtonText=L.Text(restart?"Restart":"Shut down"),CloseButtonText=L.Text("Cancel"),
            DefaultButton=ContentDialogButton.Close,
        };
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        // Diagnostics may inspect and cancel the dialog, but must never power off the user's machine.
        if(Program.IsSmokeTest||Program.IsDiagnosticProfile)return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"shutdown.exe"))
            {Arguments=restart?"/r /t 0":"/s /t 0",UseShellExecute=false,CreateNoWindow=true});
        }
        catch(Exception ex){DockApp.Current.Log.Warn("Power action failed: "+ex.Message);DockApp.Current.Notify("FloatingDock",L.Text("Could not start the power action."),true);}
    }
    internal static MenuFlyout Menu(Func<XamlRoot?> root,Action<bool>? modal=null)
    {
        var menu=new MenuFlyout();
        foreach(bool restart in new[]{false,true})
        {
            var item=new MenuFlyoutItem {Text=L.Text(restart?"Restart":"Shut down"),Icon=new FontIcon {Glyph=restart?"\uE777":"\uE7E8"}};
            item.Click+=async(_,_)=>
            {
                if(root() is not { } xaml)return;
                modal?.Invoke(true);try{await ConfirmAsync(xaml,restart);}finally{modal?.Invoke(false);}
            };menu.Items.Add(item);
        }
        menu.Opening+=(_,_)=>
        {
            ((MenuFlyoutItem)menu.Items[0]).Text=L.Text("Shut down");
            ((MenuFlyoutItem)menu.Items[1]).Text=L.Text("Restart");
        };
        return menu;
    }
}
