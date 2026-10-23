using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Reads only the shell's local, writable GETMINRECT payload; no foreign-process memory or hooks.</summary>
public static class ShellAnimationRect
{
    [StructLayout(LayoutKind.Sequential)] private struct Info { public IntPtr Window; public RECT Rect; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public IntPtr Window; public int First,Second; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, out Info info, nuint size, out nuint read);
    [DllImport("kernel32.dll", EntryPoint="ReadProcessMemory", SetLastError = true)] private static extern bool ReadHeader(IntPtr process, IntPtr address, out Header info, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, ref RECT rect, nuint size, out nuint written);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public static bool TryUpdate(IntPtr payload, Func<IntPtr, RECT?> provider, Action<IntPtr, RECT, RECT?>? diagnostic = null)
    {
        if (payload == IntPtr.Zero) return false;
        nuint headerLength=(nuint)Marshal.SizeOf<Header>();
        if(!ReadHeader(GetCurrentProcess(),payload,out var header,headerLength,out var headerRead)||headerRead!=headerLength||header.Window==IntPtr.Zero)return false;
        nuint length = (nuint)Marshal.SizeOf<Info>();
        bool fullRead=ReadProcessMemory(GetCurrentProcess(),payload,out var info,length,out var read)&&read==length;
        if(!fullRead)info=new Info{Window=header.Window,Rect=new RECT{Left=header.First,Top=header.Second}};
        var destination = provider(info.Window);
        diagnostic?.Invoke(info.Window, info.Rect, destination);
        if (destination is not { } rect || rect.Width <= 0 || rect.Height <= 0) return false;
        nuint rectLength = (nuint)Marshal.SizeOf<RECT>();
        // Packed POINTS occupy only two LONGs. Trailing storage is not part of
        // that payload and is not guaranteed to contain zeroes.
        int packedLeft=(short)info.Rect.Left,packedTop=(short)(info.Rect.Left>>16);
        int packedRight=(short)info.Rect.Top,packedBottom=(short)(info.Rect.Top>>16);
        bool packedShape=packedRight>packedLeft&&packedBottom>packedTop;
        if(!fullRead&&!packedShape)return false;
        bool fullShape=info.Rect.Right>info.Rect.Left&&info.Rect.Bottom>info.Rect.Top;
        bool encodedCoordinates=info.Rect.Left is < short.MinValue or > short.MaxValue||info.Rect.Top is < short.MinValue or > short.MaxValue;
        bool fullOnDesktop=false;
        if(fullShape&&packedShape&&encodedCoordinates)
        {
            long x=GetSystemMetrics(76),y=GetSystemMetrics(77);
            fullOnDesktop=info.Rect.Left>=x&&info.Rect.Top>=y&&info.Rect.Right<=x+GetSystemMetrics(78)&&info.Rect.Bottom<=y+GetSystemMetrics(79);
        }
        if ((packedShape&&(!fullShape||encodedCoordinates&&!fullOnDesktop))||(info.Rect.Right == 0 && info.Rect.Bottom == 0 && (info.Rect.Left != 0 || info.Rect.Top != 0)))
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
