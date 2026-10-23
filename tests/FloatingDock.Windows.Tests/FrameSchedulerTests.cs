using System.Collections.Concurrent;
using FloatingDock.Windows.WindowManagement;
using Xunit;

namespace FloatingDock.Windows.Tests;

public class FrameSchedulerTests
{
    [Fact]
    public async Task Restart_DiscardsOldWork_AndReusesCallback()
    {
        var queue = new ConcurrentQueue<Action>();
        int ticks = 0;
        using var scheduler = new FrameScheduler(action => { queue.Enqueue(action); return true; }, () => ticks++);
        scheduler.Start();
        await Task.Delay(120);
        Assert.True(queue.TryDequeue(out var old));
        scheduler.Stop(); scheduler.Start();
        old!();
        Assert.Equal(0, ticks);
        await Task.Delay(120);
        Assert.True(queue.TryDequeue(out var current));
        scheduler.Stop();
        Assert.Same(old, current);
        current!();
        Assert.Equal(0, ticks);
        scheduler.Start();
        await Task.Delay(120);
        Assert.True(queue.TryDequeue(out current));
        current!();
        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task RejectedPost_DoesNotBlockFollowingFrames()
    {
        int attempts = 0, ticks = 0;
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new FrameScheduler(action =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return false;
            posted.TrySetResult(action); return true;
        }, () => ticks++);
        scheduler.SetFrameRate(144);
        scheduler.Start();
        var callback = await posted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        callback();
        Assert.Equal(1, ticks);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task BlockedDispatcher_HasOnePendingCallback_AndStopCancelsIt()
    {
        var queue = new ConcurrentQueue<Action>();
        int ticks = 0;
        using var scheduler = new FrameScheduler(action => { queue.Enqueue(action); return true; }, () => ticks++);
        scheduler.Start();
        await Task.Delay(250);
        Assert.Single(queue);
        scheduler.Stop();
        Assert.True(queue.TryDequeue(out var pending));
        pending!();
        Assert.Equal(0, ticks);
        await Task.Delay(80);
        Assert.Empty(queue);
        scheduler.Start();
        await Task.Delay(100);
        Assert.True(queue.TryDequeue(out pending));
        pending!();
        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task Disposal_CancelsQueuedWork_AndIsIdempotent()
    {
        var queue = new ConcurrentQueue<Action>();
        int ticks = 0;
        var scheduler = new FrameScheduler(action => { queue.Enqueue(action); return true; }, () => ticks++);
        scheduler.Start();
        await Task.Delay(100);
        scheduler.Dispose(); scheduler.Dispose(); scheduler.Start();
        while (queue.TryDequeue(out var pending)) pending();
        Assert.Equal(0, ticks);
    }
}
