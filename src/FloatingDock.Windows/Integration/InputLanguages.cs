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
        return NativeMethods.PostMessage(target,0x50,IntPtr.Zero,layout); // The application owns its input language.
    }
}
