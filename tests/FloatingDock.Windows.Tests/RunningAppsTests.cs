using System.Collections.Concurrent;
using System.Diagnostics;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.Shell;
using Xunit;

namespace FloatingDock.Windows.Tests;

public sealed class RunningAppsTests
{
    [Fact]
    public void HostedAppIdentityDoesNotCauseAnUnboundedResolutionLoop()
    {
        var handle = NativeMethods.CreateWindowEx(0, "STATIC", "Owned identity regression",
            0x00CF0000, 150, 150, 400, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            NativeMethods.ShowWindow(handle, 4); // Show without changing the user's foreground.
            var queue = new ConcurrentQueue<Action>();
            int resolutions = 0;
            using var worker = new ShellWorker();
            using var service = new RunningAppsService(worker, queue.Enqueue, hwnd =>
            {
                Interlocked.Increment(ref resolutions);
                // A hosted app's logical PID differs from its frame HWND owner.
                return new(new WindowInfo(hwnd.ToInt64(), int.MaxValue, @"C:\Apps\hosted.exe",
                    "Example!App", "Owned identity regression", "ApplicationFrameWindow", false), true);
            });
            service.IncludeOwnWindow(handle);
            var clock = Stopwatch.StartNew();
            while (service.Snapshot().Count == 0 && Volatile.Read(ref resolutions) < 4 && clock.ElapsedMilliseconds < 2000)
            {
                while (queue.TryDequeue(out var action)) action();
                Thread.Sleep(5);
            }
            var tracked = Assert.Single(service.Snapshot());
            Assert.Equal(int.MaxValue, tracked.ProcessId);
            Assert.Equal(1, Volatile.Read(ref resolutions));
        }
        finally { NativeMethods.DestroyWindow(handle); }
    }
}
