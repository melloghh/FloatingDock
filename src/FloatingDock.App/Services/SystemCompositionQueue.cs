using System.Runtime.InteropServices;

namespace FloatingDock.UI.Services;

/// <summary>Windows.UI.Composition needs the system DispatcherQueue as well as WinUI's Microsoft.UI.Dispatching queue.</summary>
internal sealed class SystemCompositionQueue
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Options { public int Size, ThreadType, ApartmentType; }

    [DllImport("CoreMessaging.dll", ExactSpelling = true)]
    private static extern int CreateDispatcherQueueController(Options options, out IntPtr controller);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out FloatingDock.Windows.Native.MSG message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref FloatingDock.Windows.Native.MSG message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref FloatingDock.Windows.Native.MSG message);

    private readonly global::Windows.System.DispatcherQueueController? _controller;

    internal SystemCompositionQueue()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new Options { Size = Marshal.SizeOf<Options>(), ThreadType = 2, ApartmentType = 0 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var pointer));
        try { _controller = WinRT.MarshalInterface<global::Windows.System.DispatcherQueueController>.FromAbi(pointer); }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    }

    internal void KeepAlive() => GC.KeepAlive(_controller);

    internal void Shutdown()
    {
        if (_controller is null) return;
        var shutdown = _controller.ShutdownQueueAsync();
        var end = DateTime.UtcNow.AddSeconds(5);
        while (shutdown.Status == global::Windows.Foundation.AsyncStatus.Started && DateTime.UtcNow < end)
        {
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
            {
                if (message.message == 0x12) continue; // WM_QUIT: WinUI already stopped its loop
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
            Thread.Sleep(10);
        }
        if (shutdown.Status == global::Windows.Foundation.AsyncStatus.Completed) shutdown.GetResults();
        else Program.TraceStartup("system composition queue shutdown did not complete");
        KeepAlive();
    }
}
