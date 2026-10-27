using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Windows.Integration;

public static unsafe partial class SystemTrayOverflow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window,uint key,byte alpha,uint flags);
    /// <summary>Explorer keeps its live UIA controls, without presenting a second tray.
    /// Bounded to one discovery/action; restores acquired opacity/style leases.</summary>
    private sealed class TrayPresentationScope : IDisposable
    {
        private readonly Dictionary<IntPtr,WindowOpacityLease> _hidden=new();
        private readonly object _gate=new();
        private readonly CancellationTokenSource _stop=new();
        private readonly Thread _watcher;
        private readonly IntPtr _shell;
        internal TrayPresentationScope()
        {
            _shell=FindWindow("Shell_TrayWnd",null);
            Suppress(_shell);Suppress(Handle);
            _watcher=new Thread(()=>
            {
                // Explorer can create the overflow lazily. Observe its HWND during
                // this operation only, including the interval before UIA Invoke returns.
                var clock=System.Diagnostics.Stopwatch.StartNew();
                try {while(!_stop.IsCancellationRequested&&clock.ElapsedMilliseconds<3000){Suppress(Handle);_stop.Token.WaitHandle.WaitOne(12);}}
                finally {Restore();}
            }) {IsBackground=true,Name="FloatingDock.TrayPresentation"};
            _watcher.Start();
        }
        private void Suppress(IntPtr window)
        {
            lock(_gate)
            {
                if(window==IntPtr.Zero||_hidden.ContainsKey(window))return;
                // DWMWA_CLOAK cannot be set on Explorer's foreign-process HWND:
                // DWM returns E_ACCESSDENIED. A reversible alpha lease works here.
                if(WindowOpacityLease.TryAcquire(window) is { } lease)_hidden[window]=lease;
            }
        }
        internal bool EnableMouseInput(IntPtr window)
        {
            lock(_gate)
            {
                if(!_hidden.ContainsKey(window))return false;
                // Alpha zero is excluded from Win32 hit testing. One alpha step
                // permits the actual right-click while remaining visually imperceptible.
                return SetLayeredWindowAttributes(window,0,1,2);
            }
        }
        private void Restore()
        {
            lock(_gate)
            {
                foreach(var lease in _hidden.Values)lease.Dispose();
                _hidden.Clear();
            }
        }
        public void Dispose()
        {
            _stop.Cancel();_watcher.Join();Restore();
            _stop.Dispose();
        }
    }
}
