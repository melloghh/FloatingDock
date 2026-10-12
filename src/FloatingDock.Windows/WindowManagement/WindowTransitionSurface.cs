using System.Runtime.InteropServices;
using FloatingDock.Core.Tiling;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>A reversible opacity lease. It never changes taskbar membership or captures application pixels.</summary>
public sealed class WindowOpacityLease : IDisposable
{
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr h,out uint key,out byte alpha,out uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h,uint key,byte alpha,uint flags);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] private static extern IntPtr Ping(IntPtr h,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
    private readonly IntPtr _window;
    private readonly uint _pid,_key,_flags;
    private readonly byte _alpha;
    private readonly bool _addedLayer;
    private readonly int _transitions;
    private bool _disposed;
    private WindowOpacityLease(IntPtr window,uint pid,uint key,byte alpha,uint flags,bool added,int transitions)
    {_window=window;_pid=pid;_key=key;_alpha=alpha;_flags=flags;_addedLayer=added;_transitions=transitions;}
    public static WindowOpacityLease? TryAcquire(IntPtr window)
    {
        if(!NativeMethods.IsWindow(window)||Ping(window,0,IntPtr.Zero,IntPtr.Zero,2,25,out _)==IntPtr.Zero)return null;
        NativeMethods.GetWindowThreadProcessId(window,out var pid);
        long style=NativeMethods.GetExStyle(window);bool added=(style&0x80000)==0;
        uint key=0,flags=2;byte alpha=255;
        // UpdateLayeredWindow and color-key owners retain their own rendering policy.
        if(!added&&(!GetLayeredWindowAttributes(window,out key,out alpha,out flags)||(flags&1)!=0||alpha==0))return null;
        if(added)NativeMethods.SetWindowLongPtr(window,NativeMethods.GWL_EXSTYLE,new(style|0x80000));
        if(!SetLayeredWindowAttributes(window,key,0,2))
        {if(added)NativeMethods.SetWindowLongPtr(window,NativeMethods.GWL_EXSTYLE,new(style));return null;}
        if(NativeMethods.DwmGetWindowAttribute(window,3,out var transitions,4)<0)transitions=0;
        int disabled=1;NativeMethods.DwmSetWindowAttribute(window,3,ref disabled,4);
        return new(window,pid,key,alpha,flags,added,transitions);
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        // A hung owner must not freeze the dock's dispatcher during restoration.
        if(Ping(_window,0,IntPtr.Zero,IntPtr.Zero,2,25,out _)==IntPtr.Zero){_ = Task.Run(Restore);return;}
        Restore();
    }
    private void Restore()
    {
        NativeMethods.GetWindowThreadProcessId(_window,out var pid);if(pid!=_pid||!NativeMethods.IsWindow(_window))return;
        // Remove only the bit that this lease introduced; preserve unrelated style changes.
        SetLayeredWindowAttributes(_window,_key,_alpha,_flags);
        if(_addedLayer)NativeMethods.SetWindowLongPtr(_window,NativeMethods.GWL_EXSTYLE,new(NativeMethods.GetExStyle(_window)&~0x80000));
        int transitions=_transitions;NativeMethods.DwmSetWindowAttribute(_window,3,ref transitions,4);
    }
}

/// <summary>GPU-composed live representation on an owned, transparent, non-interactive HWND.</summary>
public sealed class WindowTransitionSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Properties {public uint Flags;public RECT Destination,Source;public byte Opacity;public int Visible,ClientOnly;}
    [StructLayout(LayoutKind.Sequential)] private struct Margins {public int Left,Right,Top,Bottom;}
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail,ref Properties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [StructLayout(LayoutKind.Sequential)] private struct Blend {public byte Operation,Flags,Alpha,Format;}
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window,IntPtr destination,in POINT position,in SIZE size,IntPtr source,in POINT origin,uint key,in Blend blend,uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc,in BITMAPINFOHEADER info,uint usage,out IntPtr pixels,IntPtr section,uint offset);
    private delegate IntPtr WindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
    private static readonly WindowProc Proc=(h,m,w,l)=>m==0x84?new(-1):m==0x21?new(3):NativeMethods.DefWindowProc(h,m,w,l);
    private const string ClassName="FloatingDock.TileTransition";
    private static bool _registered;
    private static readonly byte[] TransparentPixels=new byte[65536];
    private IntPtr _thumbnail;
    private WindowOpacityLease? _lease;
    public IntPtr Handle { get; private set; }
    public TileRect Bounds { get; private set; }
    public byte Opacity { get; private set; }=255;
    private readonly int _x,_y;
    private static bool InitializeTransparentViewport(IntPtr window,int x,int y,int width,int height)
    {
        long bytes=(long)width*height*4;if(bytes<=0||bytes>128*1024*1024)return false;
        var memory=CreateCompatibleDC(IntPtr.Zero);
        var info=new BITMAPINFOHEADER {biSize=40,biWidth=width,biHeight=-height,biPlanes=1,biBitCount=32};
        var bitmap=CreateDIBSection(memory,info,0,out var pixels,IntPtr.Zero,0);IntPtr previous=IntPtr.Zero;
        try
        {
            if(bitmap==IntPtr.Zero||pixels==IntPtr.Zero)return false;
            // Reuse a small zero block instead of allocating a monitor-sized
            // managed bitmap (and triggering LOH collections at every switch).
            for(int offset=0;offset<bytes;offset+=TransparentPixels.Length)
                Marshal.Copy(TransparentPixels,0,IntPtr.Add(pixels,offset),(int)Math.Min(TransparentPixels.Length,bytes-offset));
            previous=SelectObject(memory,bitmap);
            var position=new POINT {X=x,Y=y};var origin=new POINT();var size=new SIZE(width,height);var blend=new Blend {Alpha=255,Format=1};
            return UpdateLayeredWindow(window,IntPtr.Zero,position,size,memory,origin,0,blend,2);
        }
        finally{if(previous!=IntPtr.Zero)SelectObject(memory,previous);if(bitmap!=IntPtr.Zero)NativeMethods.DeleteObject(bitmap);if(memory!=IntPtr.Zero)DeleteDC(memory);}
    }
    private WindowTransitionSurface(IntPtr window,IntPtr thumbnail,WindowOpacityLease lease,int x,int y)
    {Handle=window;_thumbnail=thumbnail;_lease=lease;_x=x;_y=y;}
    public static WindowTransitionSurface? TryCreate(IntPtr source,TileRect from,TileRect to,WindowOpacityLease? lease=null)
    {
        bool acquired=lease is null;lease??=WindowOpacityLease.TryAcquire(source);if(lease is null)return null;
        if(!_registered)
        {
            var cls=new WNDCLASSEX {cbSize=Marshal.SizeOf<WNDCLASSEX>(),lpfnWndProc=Marshal.GetFunctionPointerForDelegate(Proc),hbrBackground=GetStockObject(4),lpszClassName=ClassName};
            if(NativeMethods.RegisterClassEx(ref cls)==0){if(acquired)lease.Dispose();return null;}_registered=true;
        }
        // A fixed transparent viewport avoids native resize/paint work on every frame.
        var monitors=Display.MonitorHelper.GetMonitors();
        int left=Math.Min(from.X,to.X),top=Math.Min(from.Y,to.Y),right=Math.Max(from.X+from.Width,to.X+to.Width),bottom=Math.Max(from.Y+from.Height,to.Y+to.Height);
        foreach(var monitor in monitors.Where(m=>m.Handle==NativeMethods.MonitorFromWindow(source,NativeMethods.MONITOR_DEFAULTTONEAREST)))
        {left=Math.Min(left,monitor.Bounds.Left);top=Math.Min(top,monitor.Bounds.Top);right=Math.Max(right,monitor.Bounds.Right);bottom=Math.Max(bottom,monitor.Bounds.Bottom);}
        var window=NativeMethods.CreateWindowEx(0x080800A8,ClassName,null,0x80000000,left,top,right-left,bottom-top,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(window!=IntPtr.Zero&&!InitializeTransparentViewport(window,left,top,right-left,bottom-top)){NativeMethods.DestroyWindow(window);if(acquired)lease.Dispose();return null;}
        var margins=new Margins {Left=-1,Right=-1,Top=-1,Bottom=-1};
        if(window==IntPtr.Zero||DwmExtendFrameIntoClientArea(window,ref margins)<0||DwmRegisterThumbnail(window,source,out var thumbnail)<0)
        {if(window!=IntPtr.Zero)NativeMethods.DestroyWindow(window);if(acquired)lease.Dispose();return null;}
        int disabled=1;NativeMethods.DwmSetWindowAttribute(window,3,ref disabled,4);
        var surface=new WindowTransitionSurface(window,thumbnail,lease,left,top);surface.Update(from,255);
        NativeMethods.ShowWindow(window,4);return surface;
    }
    public bool Update(TileRect rect,byte opacity)
    {
        Bounds=rect;Opacity=opacity;
        var properties=new Properties {Flags=1|4|8|16,Destination=new(){Left=rect.X-_x,Top=rect.Y-_y,Right=rect.X+rect.Width-_x,Bottom=rect.Y+rect.Height-_y},Opacity=opacity,Visible=1};
        return _thumbnail!=IntPtr.Zero&&DwmUpdateThumbnailProperties(_thumbnail,ref properties)>=0;
    }
    public void Dispose()
    {
        // Reveal the real window before removing the last compositor representation.
        _lease?.Dispose();_lease=null;
        if(_thumbnail!=IntPtr.Zero){DwmUnregisterThumbnail(_thumbnail);_thumbnail=IntPtr.Zero;}
        if(Handle!=IntPtr.Zero){NativeMethods.DestroyWindow(Handle);Handle=IntPtr.Zero;}
    }
}
