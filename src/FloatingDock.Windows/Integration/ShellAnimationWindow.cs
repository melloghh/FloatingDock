using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

/// <summary>Answers synchronous shell requests without waiting for WinUI layout or search work.</summary>
internal sealed class ShellAnimationWindow : IDisposable
{
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message,IntPtr hwnd,uint first,uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(in MSG message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool UnregisterClass(string name,IntPtr instance);
    private readonly ManualResetEventSlim _ready=new();
    private readonly Thread _thread;
    private readonly WndProc _proc;
    private readonly Func<IntPtr,IntPtr> _animation;
    private readonly Action _language;
    private readonly string _className="FloatingDock.ShellAnimation."+Guid.NewGuid().ToString("N");
    private Exception? _failure;
    private uint _shell,_taskbar;
    private IntPtr _handle;
    private volatile bool _registered;
    internal IntPtr Handle=>_handle;
    internal bool Registered=>_registered;
    internal ShellAnimationWindow(Func<IntPtr,IntPtr> animation,Action language)
    {
        _animation=animation;_language=language;_proc=WindowProc;
        _thread=new Thread(Run){IsBackground=true,Name="FloatingDock shell animation"};_thread.Start();
        _ready.Wait();
        if(_failure is not null)throw new InvalidOperationException("Could not create the shell animation window.",_failure);
    }
    private void Run()
    {
        var instance=NativeMethods.GetModuleHandle(null);
        try
        {
            var wc=new WNDCLASSEX{cbSize=Marshal.SizeOf<WNDCLASSEX>(),lpfnWndProc=Marshal.GetFunctionPointerForDelegate(_proc),hInstance=instance,lpszClassName=_className};
            if(NativeMethods.RegisterClassEx(ref wc)==0)throw new InvalidOperationException("Shell window registration failed.");
            _shell=NativeMethods.RegisterWindowMessage("SHELLHOOK");_taskbar=NativeMethods.RegisterWindowMessage("TaskbarCreated");
            _handle=NativeMethods.CreateWindowEx((uint)NativeMethods.WS_EX_TOOLWINDOW,_className,"FloatingDock shell animation",(uint)NativeMethods.WS_POPUP,0,0,0,0,IntPtr.Zero,IntPtr.Zero,instance,IntPtr.Zero);
            if(_handle==IntPtr.Zero)throw new InvalidOperationException("Shell window creation failed.");
            _registered=RegisterShellHookWindow(_handle);_ready.Set();
            while(GetMessage(out var message,IntPtr.Zero,0,0)>0){TranslateMessage(in message);DispatchMessage(in message);}
        }
        catch(Exception ex){_failure=ex;}
        finally
        {
            if(_registered)DeregisterShellHookWindow(_handle);_registered=false;
            if(_handle!=IntPtr.Zero)NativeMethods.DestroyWindow(_handle);
            _handle=IntPtr.Zero;UnregisterClass(_className,instance);_ready.Set();
        }
    }
    private IntPtr WindowProc(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam)
    {
        try
        {
            if(message==_shell&&wParam.ToInt64()==5)return _animation(lParam);
            if(message==_shell&&wParam.ToInt64()==8){_language();return IntPtr.Zero;}
            if(message==_taskbar){DeregisterShellHookWindow(hwnd);_registered=RegisterShellHookWindow(hwnd);return IntPtr.Zero;}
            if(message==0x10){PostQuitMessage(0);return IntPtr.Zero;}
        }
        catch {return IntPtr.Zero;}
        return NativeMethods.DefWindowProc(hwnd,message,wParam,lParam);
    }
    public void Dispose()
    {
        if(_handle!=IntPtr.Zero)NativeMethods.PostMessage(_handle,0x10,IntPtr.Zero,IntPtr.Zero);
        _thread.Join();_ready.Dispose();GC.KeepAlive(_proc);
    }
}
