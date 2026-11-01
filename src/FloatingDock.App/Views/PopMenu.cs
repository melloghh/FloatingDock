using FloatingDock.UI.MenuBar;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Views;

/// <summary>
/// The app's own context menu (dock, Launchpad). A Material 3 surface that springs out of the pointer, with one
/// highlight gliding between items. It replaces the Win32 popup menu so it looks and moves like the rest of the app.
/// </summary>
internal sealed class PopMenu
{
    private const double MenuWidth = 268;
    private static PopMenu? _shared;
    private readonly DockApp _app = DockApp.Current;
    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly Grid _host = new() { Padding = Pop.Pad, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Border _panel = new() { CornerRadius = new CornerRadius(M3.CornerL + 4), Padding = new Thickness(8), Width = MenuWidth };
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly Grid _rowsHost = new();
    private readonly GlideHighlight _glide = new();
    private readonly MenuSurfaceMotion _motion;
    private readonly List<(Button Button, int Id)> _rows = new();
    private TaskCompletionSource<int>? _result;
    private IntPtr _returnFocus;
    private int _generation, _selected = -1;
    private long _openedAt;
    private bool _visible;

    private PopMenu()
    {
        _rowsHost.Children.Add(_glide.Layer); _rowsHost.Children.Add(_list);
        _panel.Child = _rowsHost; _host.Children.Add(_panel);
        _window = new Window { Title = "FloatingDock context menu", Content = _host, SystemBackdrop = new TransparentBackdrop() };
        WindowSetup.MakeBorderless(_window); _hwnd = WindowSetup.Hwnd(_window);
        WindowStyler.MakeOverlayWindow(_hwnd, noActivate: false);
        _motion = new MenuSurfaceMotion(_hwnd, _panel);
        _host.KeyDown += OnKey;
        _window.Activated += (_, e) =>
        {
            // Light dismiss: losing focus cancels, except in the instant the window is still taking it.
            if (_visible && e.WindowActivationState == WindowActivationState.Deactivated && Environment.TickCount64 - _openedAt > 350) Close(0);
        };
    }

    /// <returns>The chosen item id, or 0 when dismissed.</returns>
    internal static IntPtr SharedHandle => _shared?._hwnd ?? IntPtr.Zero;
    internal static Task<int> ShowAsync(IReadOnlyList<MenuItemSpec> items, int? screenX = null, int? screenY = null, bool bottomAlign = true, bool rightAlign = false)
    {
        if (items.Count == 0) return Task.FromResult(0);
        _shared ??= new PopMenu();
        return _shared.Run(items, screenX, screenY, bottomAlign, rightAlign);
    }

    private Task<int> Run(IReadOnlyList<MenuItemSpec> items, int? screenX, int? screenY, bool bottomAlign, bool rightAlign)
    {
        _result?.TrySetResult(0);
        _result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_visible) _returnFocus = NativeMethods.GetForegroundWindow();
        int generation = ++_generation;
        M3.Theme(_host);
        var c = M3.Current;
        _panel.Background = M3.Brush(c.ContainerHigh, 248);
        _panel.BorderBrush = M3.Brush(c.OutlineVariant, 110); _panel.BorderThickness = new Thickness(1);
        Build(items, c);

        NativeMethods.GetCursorPos(out var cursor);
        int x = screenX ?? cursor.X, y = screenY ?? cursor.Y;
        var monitor = MonitorHelper.Describe(NativeMethods.MonitorFromPoint(new POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST))!;
        double scale = monitor.Scale;
        _panel.Measure(new global::Windows.Foundation.Size(MenuWidth, Math.Max(120, monitor.Bounds.Height / scale - 40)));
        int width = (int)Math.Ceiling(MenuWidth * scale), height = (int)Math.Ceiling(_panel.DesiredSize.Height * scale);
        int left = rightAlign ? x - width : x - width / 2;
        int top = bottomAlign ? y - height : y;
        left = Math.Clamp(left, monitor.Bounds.Left + 8, Math.Max(monitor.Bounds.Left + 8, monitor.Bounds.Right - width - 8));
        top = Math.Clamp(top, monitor.Bounds.Top + 8, Math.Max(monitor.Bounds.Top + 8, monitor.Bounds.Bottom - height - 8));
        int pad = (int)Math.Ceiling(Pop.PadDip * scale);
        WindowStyler.SetBounds(_hwnd, left - pad, top - pad, width + 2 * pad, height + 2 * pad);
        // The surface grows from the pointer: its pivot is where the click happened, in panel DIPs.
        double pivotX = Math.Clamp((x - left) / scale, 0, MenuWidth), pivotY = bottomAlign ? _panel.DesiredSize.Height : 0;
        _motion.Prepare(SurfaceMotion.Enabled ? (_visible ? _motion.Progress : 0) : 1);
        _motion.SetGeometry(width, height, scale, pivotX, pivotY);

        _visible = true; _selected = -1; _glide.Hide(); _openedAt = Environment.TickCount64;
        _host.IsHitTestVisible = true;
        _motion.Play(true, Math.Clamp(_app.Settings.Current.MenuAnimationMs * 2, 320, 640));
        _window.Activate(); WindowStyler.BringToFront(_hwnd);
        _ = FocusLater(generation);
        return _result.Task;
    }

    private async Task FocusLater(int generation)
    {
        await Task.Delay(30);
        if (generation == _generation && _visible) _host.Focus(FocusState.Programmatic);
    }

    private void Build(IReadOnlyList<MenuItemSpec> items, M3.Scheme c)
    {
        _list.Children.Clear(); _rows.Clear();
        // Never start or end with a divider, and never show two in a row.
        var cleaned = new List<MenuItemSpec>();
        foreach (var item in items)
        {
            if (item.IsSeparator && (cleaned.Count == 0 || cleaned[^1].IsSeparator)) continue;
            cleaned.Add(item);
        }
        while (cleaned.Count > 0 && cleaned[^1].IsSeparator) cleaned.RemoveAt(cleaned.Count - 1);
        foreach (var item in cleaned)
        {
            if (item.IsSeparator)
            {
                _list.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 5, 12, 5), Background = M3.Brush(c.OutlineVariant, 150) });
                continue;
            }
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = GlyphOf(item.Glyph), FontSize = 17, Foreground = M3.Brush(c.OnSurfaceVariant), Opacity = item.Glyph is null ? 0 : 1, VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = L.Text(item.Text), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = M3.Brush(c.OnSurface) };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            if (item.Checked) { var check = new FontIcon { Glyph = "", FontSize = 14, Foreground = M3.Brush(c.Primary), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(check, 2); row.Children.Add(check); }
            var button = new Button
            {
                Tag = "row", Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(14, 0, 14, 0), Height = 44, CornerRadius = new CornerRadius(M3.CornerM + 2), BorderThickness = new Thickness(0),
                IsEnabled = !item.Disabled,
            };
            GlassStyle.Button(button, flat: true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.Text(item.Text));
            int index = _rows.Count, id = item.Id;
            button.PointerEntered += (_, _) => { if (button.IsEnabled) Select(index); };
            button.Click += (_, _) => Close(id);
            _rows.Add((button, id)); _list.Children.Add(button);
        }
    }

    /// <summary>A glyph is given either as the character itself or as its hex code point ("E8A7").</summary>
    private static string GlyphOf(string? glyph)
    {
        if (string.IsNullOrEmpty(glyph)) return "•";
        if (glyph.Length == 4 && int.TryParse(glyph, System.Globalization.NumberStyles.HexNumber, null, out int code)) return ((char)code).ToString();
        return glyph;
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _selected = index;
        var c = M3.Current;
        _glide.SetColor(ColorHelper.FromArgb(34, c.OnSurface.R, c.OnSurface.G, c.OnSurface.B));
        _rowsHost.UpdateLayout();
        _glide.MoveTo(_rows[index].Button, M3.CornerM + 2);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case global::Windows.System.VirtualKey.Escape: Close(0); e.Handled = true; break;
            case global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up:
                {
                    int step = e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1, next = _selected;
                    for (int i = 0; i < _rows.Count; i++)
                    {
                        next = (next + step + _rows.Count) % _rows.Count;
                        if (_rows[next].Button.IsEnabled) { Select(next); break; }
                    }
                    e.Handled = true; break;
                }
            case global::Windows.System.VirtualKey.Enter when _selected >= 0 && _rows[_selected].Button.IsEnabled:
                Close(_rows[_selected].Id); e.Handled = true; break;
        }
    }

    private void Close(int id)
    {
        if (!_visible) return;
        _visible = false; _host.IsHitTestVisible = false;
        int generation = ++_generation;
        // The caller continues at once; the surface finishes its exit on its own.
        _result?.TrySetResult(id);
        bool focused = NativeMethods.GetForegroundWindow() == _hwnd;
        void Finish()
        {
            if (generation != _generation) return;
            _motion.Stop(); _window.AppWindow.Hide(); WindowStyler.Hide(_hwnd);
            if (focused && NativeMethods.IsWindow(_returnFocus) && NativeMethods.IsWindowVisible(_returnFocus) && !NativeMethods.IsIconic(_returnFocus)) NativeMethods.SetForegroundWindow(_returnFocus);
        }
        if (!SurfaceMotion.Enabled) Finish();
        else _motion.Play(false, Math.Clamp(_app.Settings.Current.MenuAnimationMs, 140, 260), Finish);
    }
}
