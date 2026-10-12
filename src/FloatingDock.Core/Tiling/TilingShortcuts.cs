using FloatingDock.Core.Configuration;
using FloatingDock.Core.Services;

namespace FloatingDock.Core.Tiling;
public enum TilingActionKind { Focus,Move,Resize,Workspace,Send,Float,Split,Pause,PreviousWorkspace,NextWorkspace }
public sealed record TilingShortcut(string Id,string Label,string Default,TilingActionKind Kind,TileDirection Direction=TileDirection.Left,int Workspace=0);
public static class TilingShortcuts
{
    public static IReadOnlyList<TilingShortcut> Definitions(DockSettings settings)
    {
        string modifier=settings.TilingModifier==TilingModifier.Alt?"Alt":"Ctrl+Alt+Win";
        var items=new List<TilingShortcut>();
        foreach(var (key,direction) in new[]{("H",TileDirection.Left),("J",TileDirection.Down),("K",TileDirection.Up),("L",TileDirection.Right)})
        {
            string name=direction.ToString().ToLowerInvariant();
            items.Add(new("focus."+name,"Focus "+name,modifier+"+"+key,TilingActionKind.Focus,direction));
            items.Add(new("move."+name,"Move "+name,modifier+"+Shift+"+key,TilingActionKind.Move,direction));
            items.Add(new("resize."+name,"Resize "+name,"Ctrl+Alt+Shift+"+key,TilingActionKind.Resize,direction));
        }
        for(int i=1;i<=settings.TilingWorkspaces;i++)
        {
            items.Add(new("workspace."+i,"Workspace "+i,modifier+"+"+i,TilingActionKind.Workspace,Workspace:i));
            items.Add(new("send."+i,"Send to workspace "+i,modifier+"+Shift+"+i,TilingActionKind.Send,Workspace:i));
        }
        items.Add(new("floating","Toggle floating",modifier+"+F",TilingActionKind.Float));
        items.Add(new("split","Toggle split direction",modifier+"+V",TilingActionKind.Split));
        items.Add(new("pause","Pause / resume tiling",modifier+"+P",TilingActionKind.Pause));
        items.Add(new("workspace.previous","Previous workspace","",TilingActionKind.PreviousWorkspace));
        items.Add(new("workspace.next","Next workspace","",TilingActionKind.NextWorkspace));
        return items;
    }
    public static string Effective(DockSettings settings,TilingShortcut shortcut)=>settings.TilingKeybindings.TryGetValue(shortcut.Id,out var value)?value:shortcut.Default;
    public static string? Error(DockSettings settings)
    {
        var used=new HashSet<(uint Mod,uint Key)>();
        foreach(var key in new[]{settings.LaunchpadHotkey,settings.SearchHotkey,settings.DockFocusHotkey,settings.RestoreTaskbarHotkey})
            if(HotkeyParser.TryParse(key,out var mod,out var vk))used.Add((mod,vk));
        foreach(var shortcut in Definitions(settings))
        {
            string text=Effective(settings,shortcut);if(string.IsNullOrWhiteSpace(text))continue;
            if(!HotkeyParser.TryParse(text,out var mod,out var vk))return "Not a valid shortcut. Example: Ctrl+Alt+Space";
            if(!used.Add((mod,vk)))return "This shortcut is already assigned to another action.";
        }
        return null;
    }
}
