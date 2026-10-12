using FloatingDock.Core.Configuration;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Settings;

/// <summary>The settings window. Closing it never exits the dock. Pages are rebuilt each time they are opened, so they always show current values.</summary>
internal sealed partial class SettingsWindow : IDisposable
{
    private readonly DockApp _app = DockApp.Current;
    private readonly Window _window;
    private readonly NavigationView _nav = new();
    private readonly ScrollViewer _scroll = new();
    private readonly List<Action> _pageCleanup = new();
    private readonly Dictionary<string, Func<UIElement>> _pages;
    private IntPtr _hwnd;

    public event Action<Window, object>? Closed;

    public SettingsWindow()
    {
        _pages = new()
        {
            ["Dock"] = DockPage,
            ["Appearance"] = AppearancePage,
            ["Magnification"] = MagnificationPage,
            ["Behaviour"] = BehaviourPage,
            ["Menu Bar"] = MenuBarPage,
            ["Icon Packs"] = IconPacksPage,
            ["Launchpad"] = LaunchpadPage,
            ["Search"] = SearchPage,
            ["Tiling manager"] = TilingPage,
            ["Windows Taskbar"] = TaskbarPage,
            ["About"] = AboutPage,
        };
        foreach (var name in _pages.Keys) _nav.MenuItems.Add(new NavigationViewItem { Content = L.Text(name), Tag = name, Icon = new SymbolIcon(IconFor(name)) });
        _nav.IsSettingsVisible = false;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.OpenPaneLength = 230;
        _nav.SelectionChanged += (_, e) => { if (e.SelectedItemContainer?.Tag is string t) Navigate(t); };
        _scroll.Content = new Grid();
        _nav.Content = _scroll;
        _nav.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255,18,21,27));

        // Settings is a standard framed window. An opaque root avoids feeding
        // old page/window pixels back through a whole-window blur surface.
        _window = new Window { Title = L.Text("FloatingDock settings"), Content = _nav };
        _window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(980, 740));
        _window.Closed += (s, e) => { _app.Running.ExcludeOwnWindow(_hwnd); Cleanup(); _app.Settings.Changed -= OnSettingsChanged; Closed?.Invoke(_window, e); };
        try { _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); } catch { }
        _hwnd = WindowSetup.Hwnd(_window);
        _nav.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        _app.Settings.Changed += OnSettingsChanged;
        _nav.Loaded += (_, _) => { if (_nav.SelectedItem is null) _nav.SelectedItem = _nav.MenuItems[0]; };
        _language=_app.Settings.Current.Language;ApplyTheme();
    }

    private static Symbol IconFor(string page) => page switch
    {
        "Dock" => Symbol.AllApps,
        "Appearance" => Symbol.FontColor,
        "Magnification" => Symbol.ZoomIn,
        "Behaviour" => Symbol.Setting,
        "Menu Bar" => Symbol.Clock,
        "Icon Packs" => Symbol.Pictures,
        "Launchpad" => Symbol.ViewAll,
        "Search" => Symbol.Find,
        "Tiling manager" => Symbol.ViewAll,
        "Windows Taskbar" => Symbol.Street,
        _ => Symbol.Help,
    };

    public IntPtr Handle => _hwnd;
    public XamlRoot? XamlRoot => _nav.XamlRoot;
    internal IReadOnlyList<string> NavigationLabels=>_nav.MenuItems.OfType<NavigationViewItem>().Select(i=>i.Content?.ToString()??"").ToArray();

    public void ShowSection(string? section)
    {
        _window.Activate();
        _app.Running.IncludeOwnWindow(_hwnd);
        WindowActivatorShim.Foreground(_hwnd);
        if (section is not null)
            foreach (NavigationViewItem item in _nav.MenuItems)
                if ((string)item.Tag == section) { _nav.SelectedItem = item; break; }
    }

    public void Close() => _window.Close();
    public void Dispose() => Close();

    private void ApplyTheme()
    {
        WindowSetup.ApplyTheme(_nav, _app.Settings.Current.Theme);
        ApplyCaptionTheme();
    }

    private void ApplyCaptionTheme()
    {
        bool dark = _nav.RequestedTheme == ElementTheme.Dark || _nav.RequestedTheme == ElementTheme.Default && _nav.ActualTheme == ElementTheme.Dark;
        var bg = dark ? Microsoft.UI.ColorHelper.FromArgb(255, 18, 21, 27) : Microsoft.UI.ColorHelper.FromArgb(255, 249, 249, 249);
        _nav.Background=new SolidColorBrush(bg);
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var title = _window.AppWindow.TitleBar;
        title.BackgroundColor = title.InactiveBackgroundColor = title.ButtonBackgroundColor = title.ButtonInactiveBackgroundColor = bg;
        title.ForegroundColor = title.ButtonForegroundColor = title.InactiveForegroundColor = title.ButtonInactiveForegroundColor = fg;
        title.ButtonHoverBackgroundColor = title.ButtonPressedBackgroundColor = dark ? Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58) : Microsoft.UI.ColorHelper.FromArgb(255, 224, 224, 224);
        title.ButtonHoverForegroundColor = title.ButtonPressedForegroundColor = fg;
        int darkValue = dark ? 1 : 0;
        FloatingDock.Windows.Native.NativeMethods.DwmSetWindowAttribute(_hwnd, 20, ref darkValue, sizeof(int));
    }

    private InterfaceLanguage _language=InterfaceLanguage.English;
    private void OnSettingsChanged(DockSettings s) => _app.UiQueue.TryEnqueue(()=>
    {
        ApplyTheme();
        if(_language==s.Language)return;
        _language=s.Language;_window.Title=L.Text("FloatingDock settings");
        foreach(NavigationViewItem item in _nav.MenuItems)item.Content=L.Text((string)item.Tag);
        Refresh();
    });

    private void Navigate(string name)
    {
        Cleanup();
        var content = _pages[name]();
        var host = new StackPanel { Spacing = 8, Padding = new Thickness(36, 28, 36, 40), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        host.Children.Add(content);
        _scroll.Content = host;
        host.Loaded += (_,_)=>SurfaceMotion.Enter(host,initialScale:1);
        _scroll.ChangeView(0, 0, null, true);
    }

    private void Cleanup()
    {
        foreach (var a in _pageCleanup) { try { a(); } catch { } }
        _pageCleanup.Clear();
    }

    /// <summary>Re-renders the current page after something changed behind its back.</summary>
    private void Refresh()
    {
        if (_nav.SelectedItem is NavigationViewItem { Tag: string t }) Navigate(t);
    }
    internal void RefreshTilingBindingsAfterRejection()
    {if(_nav.SelectedItem is NavigationViewItem {Tag:"Tiling manager"})Refresh();}

    private async Task<ContentDialogResult> Dialog(string title, UIElement content, string primary, string close = "Cancel")
    {
        var d = new ContentDialog { Title = L.Text(title), Content = content, PrimaryButtonText = L.Text(primary), CloseButtonText = L.Text(close), XamlRoot = _nav.XamlRoot, DefaultButton = ContentDialogButton.Primary };
        return await d.ShowAsync();
    }

    private async Task Message(string title, string text) =>
        await new ContentDialog { Title = L.Text(title), Content = new TextBlock { Text = L.Text(text), TextWrapping = TextWrapping.Wrap }, CloseButtonText = L.Text("OK"), XamlRoot = _nav.XamlRoot }.ShowAsync();
}

internal static class WindowActivatorShim
{
    public static void Foreground(IntPtr hwnd) => FloatingDock.Windows.WindowManagement.WindowStyler.BringToFront(hwnd);
}
