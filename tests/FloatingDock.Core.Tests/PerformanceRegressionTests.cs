using FloatingDock.Core.Configuration;
using FloatingDock.Core.Geometry;
using FloatingDock.Core.Models;
using Xunit;

namespace FloatingDock.Core.Tests;

public class PerformanceRegressionTests
{
    [Fact]
    public void ReusedDockLayout_DoesNotAllocateWhileMagnifying()
    {
        var settings = new DockSettings();
        var parameters = GeometryParams.From(settings);
        var metrics = WindowMetrics.Compute(parameters, 24, 4);
        var motion = new DockMotion();
        motion.SetEntries(Enumerable.Range(0, 24).Select(i => new DockEntry
        {
            Key = i.ToString(), Kind = i % 7 == 0 ? DockEntryKind.Separator : DockEntryKind.App,
        }).ToArray(), false);
        var result = new DockLayoutResult();
        // Warm type initialization and JIT before counting this thread's managed allocations.
        for (int i = 0; i < 100; i++) DockLayout.Compute(parameters, metrics, motion.Items, i, 1, result);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) DockLayout.Compute(parameters, metrics, motion.Items, i % 800, 1, result);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(result.PlateLeft));
        Assert.True(result.PlateRight > result.PlateLeft);
    }
}
