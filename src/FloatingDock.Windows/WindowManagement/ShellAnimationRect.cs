using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Reads only the shell's local, writable GETMINRECT payload; no foreign-process memory or hooks.</summary>
public static class ShellAnimationRect
{
    [StructLayout(LayoutKind.Sequential)] private struct Info { public IntPtr Window; public RECT Rect; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, out Info info, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, ref RECT rect, nuint size, out nuint written);

    public static bool TryUpdate(IntPtr payload, Func<IntPtr, RECT?> provider)
    {
        if (payload == IntPtr.Zero) return false;
        nuint length = (nuint)Marshal.SizeOf<Info>();
        if (!ReadProcessMemory(GetCurrentProcess(), payload, out var info, length, out var read) || read != length || info.Window == IntPtr.Zero) return false;
        if (provider(info.Window) is not { } rect || rect.Width <= 0 || rect.Height <= 0) return false;
        nuint rectLength = (nuint)Marshal.SizeOf<RECT>();
        // Recent shells also use two packed signed POINTS in the first two LONGs, followed by zero padding.
        if (info.Rect.Right == 0 && info.Rect.Bottom == 0 && (info.Rect.Left != 0 || info.Rect.Top != 0))
        {
            if (rect.Left is < short.MinValue or > short.MaxValue || rect.Top is < short.MinValue or > short.MaxValue ||
                rect.Right is < short.MinValue or > short.MaxValue || rect.Bottom is < short.MinValue or > short.MaxValue) return false;
            rect = new RECT
            {
                Left = (rect.Left & 0xffff) | ((rect.Top & 0xffff) << 16),
                Top = (rect.Right & 0xffff) | ((rect.Bottom & 0xffff) << 16),
            };
            rectLength = 2 * sizeof(int); // Do not touch padding beyond the two POINTS.
        }
        return WriteProcessMemory(GetCurrentProcess(), payload + IntPtr.Size, ref rect, rectLength, out var written) && written == rectLength;
    }
}
