using System.Globalization;
using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

public static class InputLanguages
{
    public sealed record Layout(IntPtr Handle,string Name,string Tag);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count,[Out] IntPtr[]? list);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    public static IReadOnlyList<Layout> Installed()
    {
        var list = new IntPtr[Math.Clamp(GetKeyboardLayoutList(0,null),0,128)];
        int n = GetKeyboardLayoutList(list.Length,list);
        return list.Take(n).Select(h =>
        {
            try { var c=CultureInfo.GetCultureInfo((int)(h.ToInt64()&0xffff));return new Layout(h,c.NativeName,c.TwoLetterISOLanguageName.ToUpperInvariant()); }
            catch(CultureNotFoundException) { return new Layout(h,"Раскладка "+h.ToString("X"),"—"); }
        }).ToArray();
    }
    public static bool Request(IntPtr target,IntPtr layout)
    {
        if(!NativeMethods.IsWindow(target)||!Installed().Any(x=>x.Handle==layout))return false;
        return NativeMethods.PostMessage(InputSite(target),0x50,IntPtr.Zero,layout); // Send to the focused input thread/site.
    }
    [StructLayout(LayoutKind.Sequential)] private struct ThreadInfo
    { public int Size,Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public RECT CaretRect; }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref ThreadInfo info);
    public static IntPtr InputSite(IntPtr target)
    {
        uint thread=NativeMethods.GetWindowThreadProcessId(target,out _);
        var info=new ThreadInfo {Size=Marshal.SizeOf<ThreadInfo>()};
        return thread!=0&&GetGUIThreadInfo(thread,ref info)&&NativeMethods.IsWindow(info.Focus)?info.Focus:target;
    }
}
