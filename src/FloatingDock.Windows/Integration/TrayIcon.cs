using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

public sealed record TrayMenuItem(int Id, string Text, bool Checked = false, bool IsSeparator = false);

/// <summary>Notification-area icon with a native popup menu. Re-adds itself when Explorer restarts.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MessageWindow _window;
    private readonly string _tooltip;
    private IntPtr _icon;
    private bool _added;
    public bool IsAdded => _added;

    public event Action? LeftClick;
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }
    public event Action<int>? MenuCommand;

    public TrayIcon(MessageWindow window, string tooltip, string? iconSourceExe)
    {
        _window = window;
        _tooltip = tooltip;
        if (!string.IsNullOrEmpty(iconSourceExe))
        {
            var large = new IntPtr[1]; var small = new IntPtr[1];
            if (NativeMethods.ExtractIconEx(iconSourceExe, 0, large, small, 1) > 0) { _icon = small[0] != IntPtr.Zero ? small[0] : large[0]; if (large[0] != IntPtr.Zero && large[0] != _icon) NativeMethods.DestroyIcon(large[0]); }
        }
        _window.TrayMessage += OnTrayMessage;
        _window.TaskbarCreated += Add;
        Add();
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_TIP | (_icon != IntPtr.Zero ? NativeMethods.NIF_ICON : 0),
        uCallbackMessage = MessageWindow.TrayCallbackMessage,
        hIcon = _icon,
        szTip = _tooltip.Length > 120 ? _tooltip[..120] : _tooltip,
        szInfo = "", szInfoTitle = "",
    };

    private void Add()
    {
        var d = Data();
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref d);
    }

    /// <summary>Shows a balloon / toast next to the tray icon (the only user-visible channel the dock has when no window is open).</summary>
    public void ShowBalloon(string title, string text, bool error)
    {
        if (!_added) return;
        var d = Data();
        d.uFlags |= NativeMethods.NIF_INFO;
        d.szInfoTitle = title.Length > 60 ? title[..60] : title;
        d.szInfo = text.Length > 250 ? text[..250] : text;
        d.dwInfoFlags = (error ? NativeMethods.NIIF_ERROR : NativeMethods.NIIF_WARNING);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref d);
    }

    private void OnTrayMessage(uint mouseMsg)
    {
        if (mouseMsg == NativeMethods.WM_LBUTTONUP) LeftClick?.Invoke();
        else if (mouseMsg == NativeMethods.WM_RBUTTONUP || mouseMsg == NativeMethods.WM_CONTEXTMENU) ShowMenu();
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items is null || items.Count == 0) return;
        var specs = items.Select(i => new MenuItemSpec(i.Id, i.Text, i.Checked, false, i.IsSeparator)).ToList();
        int cmd = NativeMenu.Show(_window.Handle, specs);
        if (cmd != 0) MenuCommand?.Invoke(cmd);
    }

    public void Dispose()
    {
        _window.TrayMessage -= OnTrayMessage;
        _window.TaskbarCreated -= Add;
        if (_added)
        {
            var d = Data();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref d);
            _added = false;
        }
        if (_icon != IntPtr.Zero) { NativeMethods.DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
