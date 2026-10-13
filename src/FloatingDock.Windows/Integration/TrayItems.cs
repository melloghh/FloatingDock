using System.Runtime.InteropServices;
using Microsoft.Win32;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

public sealed record TrayItem(string Key,string Name,byte[]? Icon,bool Overflow);

public static unsafe partial class SystemTrayOverflow
{
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetLBound(IntPtr array,uint dimension,out int lower);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetUBound(IntPtr array,uint dimension,out int upper);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayAccessData(IntPtr array,out IntPtr data);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayUnaccessData(IntPtr array);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayDestroy(IntPtr array);
    private static string IconKey(IntPtr element,string id,string name)
    {
        IntPtr array=default;
        if(((delegate* unmanaged[Stdcall]<IntPtr,IntPtr*,int>)Slot(element,4))(element,&array)<0||array==IntPtr.Zero)return id+"|"+name;
        try
        {
            if(SafeArrayGetLBound(array,1,out int lower)<0||SafeArrayGetUBound(array,1,out int upper)<0||upper-lower is <0 or >63||SafeArrayAccessData(array,out var data)<0)return id+"|"+name;
            try{var values=new int[upper-lower+1];Marshal.Copy(data,values,0,values.Length);return id+"|"+string.Join('.',values);}
            finally{SafeArrayUnaccessData(array);}
        }
        finally{SafeArrayDestroy(array);}
    }
    private sealed record IconSnapshot(string Name,string Executable,byte[] Bytes);
    private static IReadOnlyList<IconSnapshot> IconSnapshots()
    {
        var result=new List<IconSnapshot>();
        using var root=Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if(root is null)return result;
        foreach(string key in root.GetSubKeyNames().Take(512))
        {
            using var entry=root.OpenSubKey(key);
            if(entry?.GetValue("IconSnapshot") is byte[] bytes&&bytes.Length is >8 and <262144)
                result.Add(new(entry.GetValue("InitialTooltip") as string??"",entry.GetValue("ExecutablePath") as string??"",bytes));
        }
        return result;
    }
    private static bool VisitIcons(IntPtr hwnd,Func<IntPtr,string,string,bool> visitor)
    {
        if(hwnd==IntPtr.Zero)return false;
        IntPtr automation=default,walker=default,root=default;var pending=new Stack<(IntPtr Element,int Depth)>();
        try
        {
            var clsid=new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E");var iid=new Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
            if(CoCreateInstance(clsid,IntPtr.Zero,1,iid,out automation)<0||Get(automation,16,out walker)<0)return false;
            if(((delegate* unmanaged[Stdcall]<IntPtr,IntPtr,IntPtr*,int>)Slot(automation,6))(automation,hwnd,&root)<0||root==IntPtr.Zero)return false;
            pending.Push((root,0));root=IntPtr.Zero;int count=0;
            while(pending.Count>0&&count++<700)
            {
                var (element,depth)=pending.Pop();
                try
                {
                    int control=0;((delegate* unmanaged[Stdcall]<IntPtr,int*,int>)Slot(element,21))(element,&control);
                    if(control==50000&&visitor(element,String(element,29),String(element,23)))return true;
                    if(depth>=18||Child(walker,4,element,out var child)<0)continue;
                    while(child!=IntPtr.Zero&&pending.Count<700){Child(walker,6,child,out var next);pending.Push((child,depth+1));child=next;}
                    if(child!=IntPtr.Zero)Marshal.Release(child);
                }
                finally{Marshal.Release(element);}
            }
        }
        finally
        {
            foreach(var item in pending)Marshal.Release(item.Element);
            if(root!=IntPtr.Zero)Marshal.Release(root);if(walker!=IntPtr.Zero)Marshal.Release(walker);if(automation!=IntPtr.Zero)Marshal.Release(automation);
        }
        return false;
    }
    /// <summary>Read Explorer's live buttons, not historical registry entries. Registry supplies only their PNG image.</summary>
    public static IReadOnlyList<TrayItem> ReadItems()
    {
        using var presentation=new TrayPresentationScope();
        int initialized=CoInitializeEx(IntPtr.Zero,0);var result=new List<TrayItem>();
        var icons=IconSnapshots();
        try
        {
            if(Visible)Dismiss();
            bool opened=TryOpen(-10000,-10000,-9000,-9000);
            void Read(IntPtr hwnd,bool overflow)
            {
                VisitIcons(hwnd,(element,id,name)=>
            {
                if(name.Length==0)return false;
                string label=name.Trim();
                var image=icons.Where(i=>i.Name.Length>0&&(label.Equals(i.Name.Trim(),StringComparison.OrdinalIgnoreCase)||label.StartsWith(i.Name.Trim()+",",StringComparison.OrdinalIgnoreCase)||label.StartsWith(i.Name.Trim()+"\n",StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(i=>i.Name.Length).FirstOrDefault();
                image??=icons.FirstOrDefault(i=>Path.GetFileNameWithoutExtension(i.Executable) is {Length:>0} exe&&label.StartsWith(exe,StringComparison.OrdinalIgnoreCase));
                if(image is null&&(label.StartsWith("Windows Security",StringComparison.OrdinalIgnoreCase)||label.StartsWith("Безопасность Windows",StringComparison.OrdinalIgnoreCase)))image=icons.FirstOrDefault(i=>Path.GetFileName(i.Executable).Equals("SecurityHealthSystray.exe",StringComparison.OrdinalIgnoreCase));
                if(image is null&&(label.Contains("извлечение",StringComparison.OrdinalIgnoreCase)||label.Contains("Safely Remove",StringComparison.OrdinalIgnoreCase)))image=icons.FirstOrDefault(i=>Path.GetFileName(i.Executable).Equals("explorer.exe",StringComparison.OrdinalIgnoreCase)&&i.Bytes.Length>500);
                string cls=String(element,30);
                if(!overflow&&id!="NotifyItemIcon"&&!cls.Contains("NotifyIcon",StringComparison.OrdinalIgnoreCase)&&cls!="ToolbarWindow32")return false;
                result.Add(new(IconKey(element,id,name),label,image?.Bytes,overflow));return false;
            });
            }
            if(opened)Read(Handle,true);
            Read(FindWindow("Shell_TrayWnd",null),false);
            return result.DistinctBy(i=>i.Key).Take(64).ToArray();
        }
        finally{Dismiss();if(initialized>=0)CoUninitialize();}
    }
    /// <summary>Explorer still owns notification callbacks. Re-resolve on click so stale/disposed icons cannot be invoked.</summary>
    public static bool ActivateItem(TrayItem item,bool contextMenu=false,POINT? anchor=null)
    {
        using var presentation=new TrayPresentationScope();
        int initialized=CoInitializeEx(IntPtr.Zero,0);bool opened=false;
        IntPtr repositioned=IntPtr.Zero;RECT savedPosition=default;
        NativeMethods.GetCursorPos(out var contextPoint);
        contextPoint=anchor??contextPoint;
        try
        {
            if(item.Overflow){opened=TryOpen(-10000,-10000,-9000,-9000);if(!opened)return false;}
            var hwnd=item.Overflow?Handle:FindWindow("Shell_TrayWnd",null);
            bool invoked=false;
            VisitIcons(hwnd,(element,id,name)=>
            {
                if(IconKey(element,id,name)!=item.Key)return false;
                if(contextMenu)
                {
                    // Apps receive Explorer's notification-button coordinates.
                    // Place that live button at our clicked icon before invoking;
                    // moving a menu later misses menus owned by the app's process.
                    RECT buttonRect=default;
                    if(((delegate* unmanaged[Stdcall]<IntPtr,RECT*,int>)Slot(element,43))(element,&buttonRect)>=0&&buttonRect.Width>0&&NativeMethods.GetWindowRect(hwnd,out var sourceRect))
                    {
                        savedPosition=sourceRect;repositioned=hwnd;
                        NativeMethods.SetWindowPos(hwnd,IntPtr.Zero,sourceRect.Left+contextPoint.X-(buttonRect.Left+buttonRect.Width/2),sourceRect.Top+contextPoint.Y-(buttonRect.Top+buttonRect.Height/2),0,0,NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE|NativeMethods.SWP_NOZORDER);
                    }
                    ((delegate* unmanaged[Stdcall]<IntPtr,int>)Slot(element,3))(element); // SetFocus on the actual notification button
                    IntPtr site=hwnd;
                    var info=new GuiThreadInfo {Size=Marshal.SizeOf<GuiThreadInfo>()};uint thread=NativeMethods.GetWindowThreadProcessId(hwnd,out _);
                    if(GetGUIThreadInfo(thread,ref info)&&info.Focus!=IntPtr.Zero)site=info.Focus;
                    PostMessage(site,0x100,new IntPtr(0x5D),IntPtr.Zero);PostMessage(site,0x101,new IntPtr(0x5D),IntPtr.Zero);
                    invoked=true;return true;
                }
                IntPtr pattern=default;var iid=new Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059");
                if(((delegate* unmanaged[Stdcall]<IntPtr,int,Guid*,IntPtr*,int>)Slot(element,14))(element,10000,&iid,&pattern)>=0&&pattern!=IntPtr.Zero)
                {try{invoked=((delegate* unmanaged[Stdcall]<IntPtr,int>)Slot(pattern,3))(pattern)>=0;}finally{Marshal.Release(pattern);}}
                return true;
            });
            // Context menus are Explorer-owned and need its source alive until the message is handled.
            if(contextMenu&&invoked)Thread.Sleep(120);
            return invoked;
        }
        finally
        {
            if(opened){if(contextMenu){ShowWindow(Handle,0);ReleaseTemporaryTaskbar();}else Dismiss();}
            if(repositioned!=IntPtr.Zero&&NativeMethods.IsWindow(repositioned))NativeMethods.SetWindowPos(repositioned,IntPtr.Zero,savedPosition.Left,savedPosition.Top,0,0,NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE|NativeMethods.SWP_NOZORDER);
            if(initialized>=0)CoUninitialize();
        }
    }
}
