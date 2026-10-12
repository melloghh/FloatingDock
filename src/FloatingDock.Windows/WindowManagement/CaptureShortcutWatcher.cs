using System.Runtime.InteropServices;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Observes screenshot shortcuts without consuming or injecting keyboard input. Owns no polling timer.</summary>
public sealed class CaptureShortcutWatcher : IDisposable
{
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int kind,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    private readonly HookProc _proc;
    private IntPtr _hook;
    private readonly Action _capture;
    public CaptureShortcutWatcher(Action capture)
    {
        _capture=capture;_proc=OnKey;
        _hook=SetWindowsHookExW(13,_proc,GetModuleHandle(null),0);
    }
    public static bool IsCaptureShortcut(int key,bool windows,bool shift)=>key==0x2C||key==0x53&&windows&&shift;
    private IntPtr OnKey(int code,IntPtr message,IntPtr data)
    {
        if(code>=0&&message.ToInt64() is 0x100 or 0x104)
        {
            int key=Marshal.ReadInt32(data);
            if(IsCaptureShortcut(key,(GetAsyncKeyState(0x5B)&0x8000)!=0||(GetAsyncKeyState(0x5C)&0x8000)!=0,(GetAsyncKeyState(0x10)&0x8000)!=0))
                try{_capture();}catch{ /* Always pass the user's shortcut through. */ }
        }
        return CallNextHookEx(_hook,code,message,data);
    }
    public void Dispose(){if(_hook!=IntPtr.Zero){UnhookWindowsHookEx(_hook);_hook=IntPtr.Zero;}}
}
