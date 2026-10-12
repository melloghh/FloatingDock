using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;

namespace FloatingDock.Windows.WindowManagement;
/// <summary>Restorable native Windows 11 border; never changes caption/style or draws input-blocking overlays.</summary>
public sealed class WindowBorderAccent
{
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out uint value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,in uint value,int size);
    private uint? _original,_applied;
    public int LastResult { get; private set; }
    public uint? Applied=>_applied;
    public static bool Supported=>OperatingSystem.IsWindowsVersionAtLeast(10,0,22000);
    public static bool TryRead(IntPtr window,out uint color)=>DwmGetWindowAttribute(window,34,out color,4)>=0;
    public static uint Color(string text)
    {ColorUtil.TryParseRgb(text,out var rgb);return (uint)(rgb.R|(rgb.G<<8)|(rgb.B<<16));}
    public void Apply(IntPtr window,uint color)
    {
        if(_applied==color)return;
        if(!Supported)return;
        // BORDER_COLOR is documented for Set; some Windows builds reject Get.
        // In that case release our override back to DWM's default behavior.
        if(_original is null)_original=TryRead(window,out var original)?original:0xffffffff;
        LastResult=DwmSetWindowAttribute(window,34,color,4);if(LastResult>=0)_applied=color;
    }
    public void Restore(IntPtr window)
    {
        if(_original is uint original&&_applied is uint applied&&(!TryRead(window,out var current)||current==applied))DwmSetWindowAttribute(window,34,original,4);
        _original=null;_applied=null;
    }
}
