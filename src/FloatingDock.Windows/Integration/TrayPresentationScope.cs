using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

public static unsafe partial class SystemTrayOverflow
{
    /// <summary>Explorer keeps its live UIA controls, without presenting a second tray.
    /// Bounded to one discovery/action; restores only the app-cloak bit acquired here.</summary>
    private sealed class TrayPresentationScope : IDisposable
    {
        private readonly Dictionary<IntPtr,uint> _cloaked=new();
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
                try {while(!_stop.IsCancellationRequested&&clock.ElapsedMilliseconds<3000){Suppress(Handle);_stop.Token.WaitHandle.WaitOne(2);}}
                finally {Restore();}
            }) {IsBackground=true,Name="FloatingDock.TrayPresentation"};
            _watcher.Start();
        }
        private void Suppress(IntPtr window)
        {
            lock(_gate)
            {
                if(window==IntPtr.Zero||_cloaked.ContainsKey(window))return;
                if(NativeMethods.DwmGetWindowAttribute(window,14,out int flags,4)<0||(flags&1)!=0)return;
                NativeMethods.GetWindowThreadProcessId(window,out uint pid);
                int cloak=1;
                if(NativeMethods.DwmSetWindowAttribute(window,13,ref cloak,4)>=0)_cloaked[window]=pid;
            }
        }
        private void Restore()
        {
            lock(_gate)
            {
                foreach(var (window,pid) in _cloaked)
                {
                    NativeMethods.GetWindowThreadProcessId(window,out uint current);
                    if(current!=pid||!NativeMethods.IsWindow(window))continue;
                    int cloak=0;NativeMethods.DwmSetWindowAttribute(window,13,ref cloak,4);
                }
                _cloaked.Clear();
            }
        }
        public void Dispose()
        {
            _stop.Cancel();_watcher.Join();Restore();
            _stop.Dispose();
        }
    }
}
