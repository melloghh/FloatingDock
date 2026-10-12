using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

public sealed record MenuItemSpec(int Id, string Text, bool Checked = false, bool Disabled = false, bool IsSeparator = false)
{
    public static MenuItemSpec Separator { get; } = new(0, "", IsSeparator: true);
}

/// <summary>
/// Native Win32 popup menu. Unlike XAML flyouts it works reliably from windows that are never activated (the Dock), dismisses on any outside click, and
/// supports the keyboard out of the box.
/// </summary>
public static class NativeMenu
{
    /// <returns>The chosen item id, or 0 when the menu was dismissed.</returns>
    public static int Show(IntPtr ownerHwnd, IReadOnlyList<MenuItemSpec> items, int? screenX = null, int? screenY = null, bool bottomAlign = true, bool rightAlign = false)
    {
        if (items.Count == 0) return 0;
        var menu = NativeMethods.CreatePopupMenu();
        try
        {
            using var renderer = new DarkMenuRenderer(ownerHwnd, menu, items);
            int x, y;
            if (screenX is int sx && screenY is int sy) { x = sx; y = sy; }
            else { NativeMethods.GetCursorPos(out var p); x = p.X; y = p.Y; }
            NativeMethods.SetForegroundWindow(ownerHwnd);                 // lets the menu close on an outside click
            uint flagsTrack = NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | (bottomAlign ? NativeMethods.TPM_BOTTOMALIGN : 0u) | (rightAlign ? 8u : 0u);
            uint cmd = NativeMethods.TrackPopupMenu(menu, flagsTrack, x, y, 0, ownerHwnd, IntPtr.Zero);
            NativeMethods.PostMessage(ownerHwnd, 0, IntPtr.Zero, IntPtr.Zero);
            return (int)cmd;
        }
        finally { NativeMethods.DestroyMenu(menu); }
    }
}
