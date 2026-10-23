using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Dispatching;

namespace FloatingDock.UI.Services;

internal static class AnimationClock
{
    internal static FrameScheduler Create(DispatcherQueue queue, Action tick)
    {
        Action? previous = null;
        DispatcherQueueHandler? handler = null;
        return new FrameScheduler(action =>
        {
            if (!ReferenceEquals(previous, action))
            {
                previous = action;
                handler = action.Invoke;
            }
            return queue.TryEnqueue(handler!);
        }, tick);
    }
}
