using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Per-HWND compositor blur. Does not read or modify the global transparency preference.</summary>
public static class LocalWindowBlur
{
    [StructLayout(LayoutKind.Sequential)] private struct Accent { public int State,Flags;public uint Color;public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct Attribute { public int Id;public IntPtr Data;public UIntPtr Size; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left,Right,Top,Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(IntPtr hwnd,ref Attribute attribute);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
    public static bool Apply(IntPtr hwnd,bool enabled)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd,out var pid);
        if(pid!=Environment.ProcessId||!NativeMethods.IsWindow(hwnd))return false;
        var accent=new Accent { State=enabled?5:0,Flags=0,Color=0 };
        var memory=Marshal.AllocHGlobal(Marshal.SizeOf<Accent>());
        try
        {
            Marshal.StructureToPtr(accent,memory,false);
            var attr=new Attribute { Id=19,Data=memory,Size=new UIntPtr((uint)Marshal.SizeOf<Accent>()) };
            bool applied=SetWindowCompositionAttribute(hwnd,ref attr);
            int host=enabled?1:0;NativeMethods.DwmSetWindowAttribute(hwnd,17,ref host,4);
            if(enabled) { var margins=new Margins { Left=-1,Right=-1,Top=-1,Bottom=-1 };DwmExtendFrameIntoClientArea(hwnd,ref margins); }
            return applied;
        }
        catch(EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }
}


