using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FloatingDock.Windows.WindowManagement;
using Xunit;

namespace FloatingDock.Windows.Tests;

public sealed class WindowSnapshotCacheTests : IDisposable
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string text, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;
    private readonly IntPtr _window;

    public WindowSnapshotCacheTests()
    {
        // A real top-level window, far off the screen so that nothing flickers while the tests run.
        _window = CreateWindowEx(0, "STATIC", "snapshot test", WS_OVERLAPPEDWINDOW | WS_VISIBLE, -4000, -4000, 900, 600, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, _window);
    }

    public void Dispose() => DestroyWindow(_window);

    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 33);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    [Fact]
    public void A_visible_window_gets_a_small_png_that_keeps_its_proportions()
    {
        var cache = new WindowSnapshotCache();
        Assert.True(cache.Remember(_window, force: true));
        var png = cache.Get(_window.ToInt64());
        Assert.NotNull(png);
        var (w, h) = PngSize(png!);
        Assert.InRange(w, 100, 480);
        Assert.InRange(h, 60, 320);
        Assert.InRange((double)w / h, 900.0 / 600 - .08, 900.0 / 600 + .08);
    }

    [Fact]
    public void Pictures_are_not_taken_again_within_a_moment_unless_forced()
    {
        var cache = new WindowSnapshotCache();
        Assert.True(cache.Remember(_window, force: false));
        Assert.False(cache.Remember(_window, force: false));
        Assert.True(cache.Remember(_window, force: true));
    }

    [Fact]
    public void A_minimized_window_keeps_the_picture_it_had_and_gets_no_new_one()
    {
        var cache = new WindowSnapshotCache();
        Assert.True(cache.Remember(_window, force: true));
        var before = cache.Get(_window.ToInt64());
        ShowWindow(_window, 6);
        Assert.False(cache.Remember(_window, force: true));
        Assert.Same(before, cache.Get(_window.ToInt64()));
    }

    [Fact]
    public void Forgetting_a_window_drops_its_picture_and_unknown_windows_have_none()
    {
        var cache = new WindowSnapshotCache();
        Assert.Null(cache.Get(12345));
        Assert.True(cache.Remember(_window, force: true));
        cache.Forget(_window.ToInt64());
        Assert.Null(cache.Get(_window.ToInt64()));
        Assert.False(cache.Remember(IntPtr.Zero, force: true));
    }
}
