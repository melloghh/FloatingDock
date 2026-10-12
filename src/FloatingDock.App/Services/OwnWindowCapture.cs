using System.Runtime.InteropServices;
using FloatingDock.Core.Icons;
using FloatingDock.Windows.Native;

namespace FloatingDock.UI.Services;

/// <summary>Opt-in render diagnostics for this process's windows. Captures composited pixels, including alpha margins.</summary>
internal static class OwnWindowCapture
{
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    internal sealed record Frame(int Width, int Height, byte[] Pixels)
    {
        internal void Save(string path) => File.WriteAllBytes(path, PngWriter.EncodeBgra(Width, Height, Pixels, false));
    }

    internal static Frame Capture(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != Environment.ProcessId || !NativeMethods.GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("Render diagnostics may only capture this application's own windows.");
        if (rect.Width <= 0 || rect.Height <= 0 || (long)rect.Width * rect.Height > IconLimits.MaxPixels)
            throw new InvalidOperationException("Invalid diagnostic viewport.");
        DwmFlush();
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, rect.Width, rect.Height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (screen == IntPtr.Zero || memory == IntPtr.Zero || bitmap == IntPtr.Zero || !BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.Left, rect.Top, 0x40cc0020 /* SRCCOPY | CAPTUREBLT */))
                throw new InvalidOperationException("Could not capture the composited application window.");
            SelectObject(memory, previous);
            var bytes = new byte[checked(rect.Width * rect.Height * 4)];
            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = rect.Width, biHeight = -rect.Height, biPlanes = 1, biBitCount = 32 };
            if (NativeMethods.GetDIBits(screen, bitmap, 0, (uint)rect.Height, bytes, ref info, 0) != rect.Height)
                throw new InvalidOperationException("Could not read composited pixels.");
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return new Frame(rect.Width, rect.Height, bytes);
        }
        finally
        {
            SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
