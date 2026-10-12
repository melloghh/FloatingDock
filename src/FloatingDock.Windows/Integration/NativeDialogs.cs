using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

/// <summary>Plain Win32 message boxes: usable before any UI framework exists (command-line restore, startup recovery) and in error paths.</summary>
public static class NativeDialogs
{
    private const string Caption = "FloatingDock";

    public static bool Confirm(string text, uint icon = NativeMethods.MB_ICONQUESTION) =>
        NativeMethods.MessageBox(IntPtr.Zero, text, Caption, NativeMethods.MB_YESNO | icon | NativeMethods.MB_SETFOREGROUND | NativeMethods.MB_TOPMOST) == NativeMethods.IDYES;

    public static void Error(string text) =>
        NativeMethods.MessageBox(IntPtr.Zero, text, Caption, NativeMethods.MB_OK | NativeMethods.MB_ICONERROR | NativeMethods.MB_SETFOREGROUND | NativeMethods.MB_TOPMOST);

    public static void Info(string text) =>
        NativeMethods.MessageBox(IntPtr.Zero, text, Caption, NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION | NativeMethods.MB_SETFOREGROUND | NativeMethods.MB_TOPMOST);
}
