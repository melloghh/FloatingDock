using System.Numerics;
using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace FloatingDock.UI.MenuBar;

/// <summary>Transparent Win32 input over the static XAML bar. Mouse movement never enters a XAML input site.</summary>
internal sealed class MenuBarPointerSurface : IDisposable
{
    private readonly NativeDockWindow _window=new();
    private readonly Compositor _compositor=new();
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root;
    private readonly ShapeVisual _highlight;
    private readonly CompositionRoundedRectangleGeometry _highlightGeometry;
    private (int W,int H,int Radius)? _region;
    [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
    {
        public uint Size,Flags;public IntPtr Window;public UIntPtr Id;public RECT Bounds;
        public IntPtr Instance,Text,Parameter,Reserved;
    }
    [DllImport("comctl32.dll")] private static extern void InitCommonControls();
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendTool(IntPtr hwnd,uint message,IntPtr value,ref ToolInfo info);
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendValue(IntPtr hwnd,uint message,IntPtr value,IntPtr data);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd,string app,string? part);
    private IntPtr _tipWindow,_tipText;
    private ToolInfo _tool;
    private bool _tipVisible;
    internal string TipDiagnostic=>$"hwnd={_tipWindow}, visible={NativeMethods.IsWindowVisible(_tipWindow)}, size={_tool.Size}";
    internal IntPtr Handle=>_window.Handle;
    internal IntPtr TipHandle=>_tipWindow;
    internal event Action<DockPointerEvent>? Pointer;

    internal unsafe MenuBarPointerSurface(IntPtr accessibilityHost)
    {
        _window.AccessibilityHost=accessibilityHost;
        _window.InputFailed+=ex=>DockApp.TryLog("menu bar input failed",ex);
        _window.Pointer+=pointer=>Pointer?.Invoke(pointer);
        IntPtr compositor=WinRT.MarshalInspectable<Compositor>.FromManaged(_compositor),interop=IntPtr.Zero,target=IntPtr.Zero;
        try
        {
            var iid=new Guid("29e691fa-4567-4dca-b319-d0f207eb6807");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositor,in iid,out interop));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr,IntPtr,int,IntPtr*,int>)(*(IntPtr**)interop)[3])(interop,Handle,1,&target));
            _target=WinRT.MarshalInspectable<DesktopWindowTarget>.FromAbi(target);
            _root=_compositor.CreateContainerVisual();_target.Root=_root;
            _highlight=_compositor.CreateShapeVisual();_highlight.Opacity=0;
            _highlightGeometry=_compositor.CreateRoundedRectangleGeometry();
            var shape=_compositor.CreateSpriteShape(_highlightGeometry);shape.FillBrush=_compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(255,255,255,255));
            _highlight.Shapes.Add(shape);_root.Children.InsertAtTop(_highlight);
        }
        catch {_window.Dispose();_compositor.Dispose();throw;}
        finally {if(target!=IntPtr.Zero)Marshal.Release(target);if(interop!=IntPtr.Zero)Marshal.Release(interop);Marshal.Release(compositor);}
    }

    internal void Sync(IntPtr host,double scale)
    {
        if(!NativeMethods.IsWindowVisible(host)||!NativeMethods.GetWindowRect(host,out var bounds))
        {WindowStyler.Hide(Handle);return;}
        // Only called for geometry/visibility changes, never for mouse movement.
        NativeMethods.SetWindowPos(Handle,NativeMethods.HWND_TOPMOST,bounds.Left,bounds.Top,bounds.Width,bounds.Height,NativeMethods.SWP_NOACTIVATE);
        _root.Size=new(bounds.Width,bounds.Height);
        var region=(bounds.Width,bounds.Height,(int)Math.Round(14*scale));
        if(_region!=region){WindowStyler.SetRoundedRegion(Handle,region.Item1,region.Item2,region.Item3);_region=region;}
        WindowStyler.Show(Handle,false);
    }
    internal void Capture()=>_window.Capture();
    internal void Release()=>_window.Release();
    internal void ShowTip(string text,global::Windows.Foundation.Rect anchor,double scale)
    {
        if(_tipWindow==IntPtr.Zero)
        {
            InitCommonControls();
            _tipWindow=NativeMethods.CreateWindowEx(0x08000088,"tooltips_class32","",0x80000003,0,0,0,0,Handle,IntPtr.Zero,NativeMethods.GetModuleHandle(null),IntPtr.Zero);
            if(_tipWindow==IntPtr.Zero)return;
            SetWindowTheme(_tipWindow,"DarkMode_Explorer",null);
            _tipText=Marshal.StringToHGlobalUni(text);
            // V2 size is accepted by both common-controls generations. Reserved is unused.
            _tool=new(){Size=(uint)(Marshal.SizeOf<ToolInfo>()-IntPtr.Size),Flags=0x1A2,Window=Handle,Id=new(1),Text=_tipText};
            if(SendTool(_tipWindow,0x432,IntPtr.Zero,ref _tool)==IntPtr.Zero)DockApp.Current.Log.Warn("Could not register bar tooltip."); // TTM_ADDTOOLW
            SendValue(_tipWindow,0x418,IntPtr.Zero,new IntPtr((int)(320*scale))); // TTM_SETMAXTIPWIDTH
        }
        var previous=_tipText;_tipText=Marshal.StringToHGlobalUni(text);_tool.Text=_tipText;
        SendTool(_tipWindow,0x439,IntPtr.Zero,ref _tool); // TTM_UPDATETIPTEXTW
        if(previous!=IntPtr.Zero)Marshal.FreeHGlobal(previous);
        if(!NativeMethods.GetWindowRect(Handle,out var bounds))return;
        int x=bounds.Left+(int)((anchor.X+anchor.Width/2)*scale),y=bounds.Bottom+(int)(6*scale);
        SendValue(_tipWindow,0x412,IntPtr.Zero,new IntPtr((y<<16)|(x&0xffff))); // TTM_TRACKPOSITION
        SendTool(_tipWindow,0x411,new IntPtr(1),ref _tool);_tipVisible=true; // TTM_TRACKACTIVATE
    }
    internal void HideTip(){if(!_tipVisible)return;SendTool(_tipWindow,0x411,IntPtr.Zero,ref _tool);_tipVisible=false;}
    internal void Highlight(global::Windows.Foundation.Rect? bounds,double scale,bool pressed=false)
    {
        if(bounds is not { } rect){_highlight.Opacity=0;return;}
        _highlight.Offset=new((float)(rect.X*scale),(float)(rect.Y*scale),0);
        _highlight.Size=new((float)(rect.Width*scale),(float)(rect.Height*scale));
        _highlightGeometry.Size=_highlight.Size;_highlightGeometry.CornerRadius=new((float)(8*scale));
        _highlight.Opacity=pressed ? .18f : .10f;
    }
    public void Dispose(){HideTip();if(_tipWindow!=IntPtr.Zero)NativeMethods.DestroyWindow(_tipWindow);if(_tipText!=IntPtr.Zero)Marshal.FreeHGlobal(_tipText);_target.Root=null;_highlight.Dispose();_highlightGeometry.Dispose();_root.Dispose();_target.Dispose();_window.Dispose();_compositor.Dispose();}
}
