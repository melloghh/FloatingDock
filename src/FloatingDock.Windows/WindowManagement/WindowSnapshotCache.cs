using System.Runtime.InteropServices;
using FloatingDock.Core.Icons;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>
/// Small pictures of what a window showed the last time it was seen, kept in memory only. DWM draws nothing live for a minimized window,
/// so the dock's preview shows this picture instead of an empty card. A picture is taken just as a window starts to minimize and when
/// another window takes the focus from it; a window that is minimized never gets one.
/// </summary>
public sealed class WindowSnapshotCache
{
    private const int MaxEntries = 24, MaxWidth = 480, MaxHeight = 320, MinGapMs = 2000;

    private readonly object _gate = new();
    private readonly Dictionary<long, byte[]> _png = new();
    private readonly Dictionary<long, long> _taken = new();
    private readonly List<long> _order = new();

    /// <summary>Takes a picture of the window unless one was taken a moment ago (or [force]); true when the cache has a new one.</summary>
    public bool Remember(IntPtr hwnd, bool force)
    {
        long id = hwnd.ToInt64();
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (!force && _taken.TryGetValue(id, out var last) && now - last < MinGapMs) return false;
            _taken[id] = now;
        }
        byte[]? png;
        try { png = Capture(hwnd); }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or ArgumentException or InvalidOperationException) { return false; }
        if (png is null) return false;
        lock (_gate)
        {
            _png[id] = png;
            _order.Remove(id);
            _order.Add(id);
            while (_order.Count > MaxEntries)
            {
                long oldest = _order[0];
                _order.RemoveAt(0);
                _png.Remove(oldest);
                _taken.Remove(oldest);
            }
        }
        return true;
    }

    /// <summary>The PNG of the last picture of the window, or null.</summary>
    public byte[]? Get(long id)
    {
        lock (_gate) return _png.TryGetValue(id, out var png) ? png : null;
    }

    /// <summary>The window is gone: its picture goes with it.</summary>
    public void Forget(long id)
    {
        lock (_gate)
        {
            _png.Remove(id);
            _taken.Remove(id);
            _order.Remove(id);
        }
    }

    // ------------------------------------------------------------------ capture

    private static byte[]? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsHungAppWindow(hwnd)) return null;
        if (!GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 64 || h < 64 || w > 8192 || h > 8192) return null;

        var info = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            dib = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = SelectObject(mem, dib);
            // PW_RENDERFULLCONTENT: windows drawn with DirectComposition (browsers, Electron apps) are included.
            if (!PrintWindow(hwnd, mem, 2)) return null;
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return Shrink(pixels, w, h);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Averages the pixels down to at most MaxWidth x MaxHeight and encodes the result; null for a picture that is all black.</summary>
    private static byte[]? Shrink(byte[] src, int w, int h)
    {
        double f = Math.Min(1.0, Math.Min((double)MaxWidth / w, (double)MaxHeight / h));
        int nw = Math.Max(1, (int)(w * f)), nh = Math.Max(1, (int)(h * f));
        var dst = new byte[nw * nh * 4];
        long light = 0;
        for (int y = 0; y < nh; y++)
        {
            int y0 = (int)((long)y * h / nh), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * h / nh));
            for (int x = 0; x < nw; x++)
            {
                int x0 = (int)((long)x * w / nw), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * w / nw));
                long b = 0, g = 0, rr = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int i = (sy * w + x0) * 4;
                    for (int sx = x0; sx < x1; sx++, i += 4) { b += src[i]; g += src[i + 1]; rr += src[i + 2]; }
                }
                int n = (y1 - y0) * (x1 - x0), o = (y * nw + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(rr / n); dst[o + 3] = 255;
                light += dst[o] + dst[o + 1] + dst[o + 2];
            }
        }
        // A window that could not draw itself comes back black: that is not a picture worth showing.
        return light < (long)nw * nh * 3 ? null : PngWriter.EncodeBgra(nw, nh, dst, premultiplied: false);
    }

    // ------------------------------------------------------------------ native

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
