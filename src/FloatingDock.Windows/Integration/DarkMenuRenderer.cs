using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

/// <summary>Owner drawing and brushes apply only while this process's popup is open.</summary>
internal sealed class DarkMenuRenderer : IDisposable
{
    private delegate IntPtr SubclassProc(IntPtr h, uint m, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Measure { public uint Type, Control, Item, Width, Height; public UIntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Draw { public uint Type, Control, Item, Action, State; public IntPtr Menu, Dc; public RECT Rect; public UIntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct MenuInfo { public uint Size, Mask, Style, MaxHeight; public IntPtr Brush; public uint Help; public UIntPtr Data; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct ItemInfo { public uint Size, Mask, Type, State, Id; public IntPtr Submenu, Checked, Unchecked; public UIntPtr Data; public IntPtr Text; public uint Length; public IntPtr Bitmap; }
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id, UIntPtr d);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetMenuInfo(IntPtr m, ref MenuInfo i);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SetMenuItemInfo(IntPtr m, uint n, bool pos, ref ItemInfo i);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern int FrameRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int len, ref RECT rect, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint c);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint c);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc,int mode);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr dc,int left,int top,int right,int bottom,int ellipseWidth,int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateFont(int h,int w,int e,int o,int weight,uint italic,uint underline,uint strike,uint charset,uint output,uint clip,uint quality,uint pitch,string face);
    private readonly IntPtr _owner, _menu, _background, _hover, _line, _font, _iconFont;
    private readonly SubclassProc _proc;
    private readonly WinEventProc _popupEvent;
    private readonly SubclassProc _popupProc;
    private IntPtr _hook, _popup;
    private bool _disposed;
    private readonly Dictionary<uint, MenuItemSpec> _items = new();
    private readonly UIntPtr _id = new(0x46444d);
    private readonly float _scale;
    private int _width;

    internal DarkMenuRenderer(IntPtr owner, IntPtr menu, IReadOnlyList<MenuItemSpec> items)
    {
        _owner=owner; _menu=menu; _scale=Math.Max(1,GetDpiForWindow(owner)/96f); _proc=Handle;
        _popupProc=PopupMessage;
        _popupEvent=(_,_,h,_,_,_,_)=>
        {
            if(_disposed||h==IntPtr.Zero||NativeMethods.GetClass(h)!="#32768")return;
            NativeMethods.GetWindowThreadProcessId(h,out var pid);if(pid!=Environment.ProcessId)return;
            _popup=h;
            int dark=1,round=2,border=0x3b3835;
            NativeMethods.DwmSetWindowAttribute(h,20,ref dark,4);
            NativeMethods.DwmSetWindowAttribute(h,33,ref round,4);
            NativeMethods.DwmSetWindowAttribute(h,34,ref border,4);
            SetWindowSubclass(h,_popupProc,_id,UIntPtr.Zero);
            NativeMethods.SetWindowPos(h,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
        };
        _background=CreateSolidBrush(0x211e1b); _hover=CreateSolidBrush(0x443c34); _line=CreateSolidBrush(0x3b3835);
        _font=CreateFont(-(int)Math.Ceiling(13*_scale),0,0,0,400,0,0,0,1,0,0,5,0,"Segoe UI");
        _iconFont=CreateFont(-(int)Math.Ceiling(15*_scale),0,0,0,400,0,0,0,1,0,0,5,0,"Segoe Fluent Icons");
        var dc=NativeMethods.GetDC(owner); var old=SelectObject(dc,_font);
        try
        {
            _width=(int)(232*_scale);
            for(int n=0;n<items.Count;n++)
            {
                var it=items[n]; uint id=it.IsSeparator ? (uint)(0x7000+n) : (uint)it.Id;
                _items[id]=it;
                uint flags=NativeMethods.MF_STRING | (it.Checked ? NativeMethods.MF_CHECKED:0) | (it.Disabled||it.IsSeparator ? 1u:0);
                NativeMethods.AppendMenu(menu,flags,(UIntPtr)id,it.Text);
                var info=new ItemInfo { Size=(uint)Marshal.SizeOf<ItemInfo>(),Mask=0x100,Type=0x100 };
                if(!SetMenuItemInfo(menu,(uint)n,true,ref info)) throw new InvalidOperationException("Could not style popup item.");
                var r=new RECT(); DrawText(dc,it.Text,it.Text.Length,ref r,0x420);
                _width=Math.Max(_width,r.Width+(int)(62*_scale));
            }
            var menuInfo=new MenuInfo { Size=(uint)Marshal.SizeOf<MenuInfo>(),Mask=2,Brush=_background };
            SetMenuInfo(menu,ref menuInfo);
            if(!SetWindowSubclass(owner,_proc,_id,UIntPtr.Zero)) throw new InvalidOperationException("Could not style popup owner.");
            uint thread=NativeMethods.GetWindowThreadProcessId(owner,out _);
            _hook=NativeMethods.SetWinEventHook(6,6,IntPtr.Zero,_popupEvent,(uint)Environment.ProcessId,thread,0);
        }
        catch { Dispose(); throw; }
        finally { SelectObject(dc,old); NativeMethods.ReleaseDC(owner,dc); }
    }

    private IntPtr PopupMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data)
    {
        var result=DefSubclassProc(hwnd,msg,w,l);
        if(msg==0x85 && !_disposed && NativeMethods.GetWindowRect(hwnd,out var bounds))
        {
            var dc=GetWindowDC(hwnd);
            if(dc!=IntPtr.Zero)
            {
                try { var rect=new RECT { Left=0,Top=0,Right=bounds.Width,Bottom=bounds.Height };FrameRect(dc,ref rect,_line); }
                finally { NativeMethods.ReleaseDC(hwnd,dc); }
            }
        }
        return result;
    }

    private IntPtr Handle(IntPtr hwnd,uint msg,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data)
    {
        try
        {
            if(msg is 0x121 or 0x11f)
            {
                if(_popup==IntPtr.Zero)
                    NativeMethods.EnumWindows((h,_)=> { NativeMethods.GetWindowThreadProcessId(h,out var pid);if(pid==Environment.ProcessId&&NativeMethods.IsWindowVisible(h)&&NativeMethods.GetClass(h)=="#32768") { _popup=h;return false; }return true; },IntPtr.Zero);
                if(_popup!=IntPtr.Zero)NativeMethods.SetWindowPos(_popup,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
            }
            if(msg==0x2c && l!=IntPtr.Zero)
            {
                var m=Marshal.PtrToStructure<Measure>(l);
                if(m.Type==1 && _items.TryGetValue(m.Item,out var it))
                {
                    m.Width=(uint)_width; m.Height=(uint)Math.Ceiling((it.IsSeparator?11:33)*_scale);
                    Marshal.StructureToPtr(m,l,false); return new IntPtr(1);
                }
            }
            if(msg==0x2b && l!=IntPtr.Zero)
            {
                var d=Marshal.PtrToStructure<Draw>(l);
                if(d.Type==1 && d.Menu==_menu && _items.TryGetValue(d.Item,out var it))
                {
                    var rect=d.Rect; FillRect(d.Dc,ref rect,_background);
                    if((d.State&1)!=0 && !it.Disabled&&!it.IsSeparator)
                    {
                        int inset=(int)Math.Ceiling(5*_scale),radius=(int)Math.Ceiling(12*_scale);
                        var brush=SelectObject(d.Dc,_hover);var pen=SelectObject(d.Dc,GetStockObject(8));
                        RoundRect(d.Dc,rect.Left+inset,rect.Top+2,rect.Right-inset,rect.Bottom-2,radius,radius);
                        SelectObject(d.Dc,pen);SelectObject(d.Dc,brush);
                    }
                    if(it.IsSeparator) { rect.Left+=(int)(12*_scale);rect.Right-=(int)(12*_scale);rect.Top=(rect.Top+rect.Bottom)/2;rect.Bottom=rect.Top+1; FillRect(d.Dc,ref rect,_line); }
                    else
                    {
                        var old=SelectObject(d.Dc,_font);int oldBk=SetBkMode(d.Dc,1);uint oldColor=SetTextColor(d.Dc,it.Disabled?0x858585u:0xf2f2f2u);
                        try
                        {
                            rect.Left+=(int)(40*_scale);rect.Right-=(int)(16*_scale);
                            DrawText(d.Dc,it.Text,it.Text.Length,ref rect,0x24);
                            rect.Left=d.Rect.Left+(int)(12*_scale);rect.Right=d.Rect.Left+(int)(32*_scale);
                            SelectObject(d.Dc,_iconFont);SetTextColor(d.Dc,it.Disabled?0x858585u:0xd9c4b0u);
                            string glyph=it.Checked?"\uE73E":Glyph(it.Text);DrawText(d.Dc,glyph,glyph.Length,ref rect,0x25);
                        }
                        finally { SetTextColor(d.Dc,oldColor);SetBkMode(d.Dc,oldBk);SelectObject(d.Dc,old); }
                    }
                    return new IntPtr(1);
                }
            }
        }
        catch { /* preserve the native menu loop */ }
        return DefSubclassProc(hwnd,msg,w,l);
    }

    private static string Glyph(string text)
    {
        var t=text.ToLowerInvariant();
        return t.Contains("quit")?"\uE7E8":t.Contains("close")?"\uE711":t.Contains("settings")?"\uE713":t.Contains("launchpad")?"\uE80A":t.Contains("separator")?"\uE921":t.Contains("location")?"\uE8B7":t.Contains("reset")?"\uE777":t.Contains("icon")?"\uE790":t.Contains("pin")?"\uE718":t.Contains("new window")?"\uE8A7":t.Contains("hide")?"\uE890":"\uE10C";
    }

    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        if(_hook!=IntPtr.Zero)NativeMethods.UnhookWinEvent(_hook);
        if(_popup!=IntPtr.Zero&&NativeMethods.IsWindow(_popup))RemoveWindowSubclass(_popup,_popupProc,_id);
        RemoveWindowSubclass(_owner,_proc,_id);
        foreach(var o in new[]{_font,_iconFont,_line,_hover,_background}) if(o!=IntPtr.Zero) DeleteObject(o);
    }
}

