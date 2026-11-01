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
    private readonly Grid _nav = new();
    private readonly StackPanel _rail = new() { Spacing = 4 };
    private readonly Grid _railGrid = new();
    private readonly GlideHighlight _railGlide = new();
    private readonly List<Button> _navButtons = new();
    private readonly ScrollViewer _scroll = new();
    private readonly List<Action> _pageCleanup = new();
    private readonly Dictionary<string, Func<UIElement>> _pages;
    private string? _current;
    private IntPtr _hwnd;
    private bool _closed;
    private readonly GlassBackdrop _glass=new();

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
        // Layout: a rounded navigation rail on the left, the page on the surface to its right.
        _nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });
        _nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var railHost = new Border { Margin = new Thickness(12, 12, 0, 12), Padding = new Thickness(12, 14, 12, 12), CornerRadius = new CornerRadius(M3.CornerXl) };
        _railGrid.Children.Add(_railGlide.Layer); _railGrid.Children.Add(_rail);
        var railScroll = new ScrollViewer { Content = _railGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        railHost.Child = railScroll;
        _railHost = railHost;
        foreach (var name in _pages.Keys) _navButtons.Add(NavButton(name));
        BuildRail();
        _nav.Children.Add(railHost);
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.Content = new Grid();
        Grid.SetColumn(_scroll, 1);
        _nav.Children.Add(_scroll);
        _scroll.SizeChanged += (_, e) => { if (_scroll.Content is Grid { Children.Count: > 0 } w && w.Children[0] is StackPanel h && e.NewSize.Width > 0) h.Width = Math.Min(860, e.NewSize.Width); };

        _window = new Window { Title = L.Text("FloatingDock settings"), Content = _nav,SystemBackdrop=_glass };
        _window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1040, 760));
        _window.Closed += (s, e) => { _closed=true; _app.Running.ExcludeOwnWindow(_hwnd); Cleanup(); _app.Settings.Changed -= OnSettingsChanged; Closed?.Invoke(_window, e); };
        try { _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); } catch { }
        _hwnd = WindowSetup.Hwnd(_window);
        _nav.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        _app.Settings.Changed += OnSettingsChanged;
        _nav.Loaded += (_, _) => { if (_current is null) Select(_pages.Keys.First()); };
        _language=_app.Settings.Current.Language;ApplyTheme();
    }

    private readonly Border _railHost;
    private M3.Scheme? _scheme = M3.Current;

    private static string IconFor(string page) => page switch
    {
        "Dock" => "",
        "Appearance" => "",
        "Magnification" => "",
        "Behaviour" => "",
        "Menu Bar" => "",
        "Icon Packs" => "",
        "Launchpad" => "",
        "Search" => "",
        "Tiling manager" => "",
        "Windows Taskbar" => "",
        "About" => "",
        _ => "",
    };

    /// <summary>A 56 px navigation pill: icon + label, the active page is filled with the primary colour.</summary>
    private Button NavButton(string name)
    {
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new FontIcon { Glyph = IconFor(name), FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = L.Text(name), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        var b = new Button
        {
            Content = content, Tag = name, Height = 52, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(26), BorderThickness = new Thickness(0),
            Style = (Style)Application.Current.Resources["M3ButtonStyle"],
        };
        b.Click += (_, _) => Select(name);
        return b;
    }

    private void BuildRail()
    {
        _rail.Children.Clear();
        _rail.Children.Add(new TextBlock
        {
            Text = "FloatingDock", FontSize = 20, FontWeight = new global::Windows.UI.Text.FontWeight { Weight = 500 },
            Margin = new Thickness(18, 4, 0, 16), Foreground = M3.Text(),
        });
        foreach (var b in _navButtons) _rail.Children.Add(b);
        StyleRail();
    }

    private void StyleRail()
    {
        var c = M3.Current;
        _railHost.Background = M3.Card();
        foreach (var b in _navButtons)
        {
            bool active = (string)b.Tag == _current;
            b.Background = M3.Clear;
            if (active) { _railGrid.UpdateLayout(); _railGlide.SetColor(c.Primary); _railGlide.MoveTo(b, 26); }
            b.Foreground = M3.Brush(active ? c.OnPrimary : c.OnSurfaceVariant);
            b.FontWeight = new global::Windows.UI.Text.FontWeight { Weight = (ushort)(active ? 600 : 500) };
        }
        _scroll.Background = M3.Clear;
    }

    public IntPtr Handle => _hwnd;
    public XamlRoot? XamlRoot => _nav.XamlRoot;
    internal IReadOnlyList<string> NavigationLabels=>_navButtons.Select(b=>((TextBlock)((Grid)b.Content).Children[1]).Text).ToArray();

    public void ShowSection(string? section)
    {
        if(_closed)return;
        _window.Activate();
        _app.Running.IncludeOwnWindow(_hwnd);
        WindowActivatorShim.Foreground(_hwnd);
        if (section is not null && _pages.ContainsKey(section)) Select(section);
    }

    public void Close() {if(_closed)return;_closed=true;_window.Close();}
    public void Dispose() => Close();

    private void ApplyTheme()
    {
        if(_closed)return;
        M3.Theme(_nav);
        _nav.Background = M3.Window();
        StyleRail();
        ApplyCaptionTheme();
    }

    private void ApplyCaptionTheme()
    {
        if(_closed)return;
        var c = M3.Current;
        bool dark = c.Dark;
        var bg = c.ContainerLow;
        var fg = c.OnSurface;
        var title = _window.AppWindow.TitleBar;
        title.BackgroundColor = title.InactiveBackgroundColor = title.ButtonBackgroundColor = title.ButtonInactiveBackgroundColor = bg;
        title.ForegroundColor = title.ButtonForegroundColor = title.InactiveForegroundColor = title.ButtonInactiveForegroundColor = fg;
        title.ButtonHoverBackgroundColor = title.ButtonPressedBackgroundColor = c.ContainerHighest;
        title.ButtonHoverForegroundColor = title.ButtonPressedForegroundColor = fg;
        int darkValue = dark ? 1 : 0;
        FloatingDock.Windows.Native.NativeMethods.DwmSetWindowAttribute(_hwnd, 20, ref darkValue, sizeof(int));
    }

    private InterfaceLanguage _language=InterfaceLanguage.English;
    private void OnSettingsChanged(DockSettings s) => _app.UiQueue.TryEnqueue(()=>
    {
        if(_closed)return;
        // A new seed or theme changes every colour baked into the current page, so rebuild it once.
        bool recoloured = !ReferenceEquals(_scheme, M3.Current);
        _scheme = M3.Current;
        ApplyTheme();
        if(_language==s.Language)
        {
            if (recoloured) Refresh();
            return;
        }
        _language=s.Language;_window.Title=L.Text("FloatingDock settings");
        foreach(var b in _navButtons)((TextBlock)((Grid)b.Content).Children[1]).Text=L.Text((string)b.Tag);
        Refresh();
    });

    private void Select(string name)
    {
        if(_closed||!_pages.ContainsKey(name))return;
        _current = name;
        StyleRail();
        Navigate(name);
    }

    private void Navigate(string name)
    {
        if(_closed)return;
        Cleanup();
        var content = _pages[name]();
        var host = new StackPanel { Spacing = 8, Padding = new Thickness(32, 28, 40, 40), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        host.Children.Add(content);
        // Fill the pane up to the reading width so every card has the same width on every page.
        if (_scroll.ActualWidth > 0) host.Width = Math.Min(860, _scroll.ActualWidth);
        // The wrapper carries the rise and the blurred copy; the page itself only fades.
        var wrapper = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        wrapper.Children.Add(host);
        _scroll.Content = wrapper;
        host.Loaded += (_,_)=>{if(!_closed){Pop.Reveal(wrapper,host,650,22,10);int index=0;foreach(var child in ((Panel)content).Children)if(child is FrameworkElement part)SurfaceMotion.Reveal(part,index++);}};
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
        if(_closed)return;
        if (_current is not null) Navigate(_current);
    }
    internal void RefreshTilingBindingsAfterRejection()
    {if(_current=="Tiling manager")Refresh();}

    private async Task<ContentDialogResult> Dialog(string title, UIElement content, string primary, string close = "Cancel")
    {
        if(_closed||_nav.XamlRoot is null)return ContentDialogResult.None;
        var d = new ContentDialog { Title = L.Text(title), Content = content, PrimaryButtonText = L.Text(primary), CloseButtonText = L.Text(close), XamlRoot = _nav.XamlRoot, DefaultButton = ContentDialogButton.Primary };
        M3Dialog.Style(d);
        return await d.ShowAsync();
    }

    private async Task Message(string title, string text)
    {
        if(_closed||_nav.XamlRoot is null)return;
        var d = new ContentDialog { Title = L.Text(title), Content = new TextBlock { Text = L.Text(text), TextWrapping = TextWrapping.Wrap }, CloseButtonText = L.Text("OK"), XamlRoot = _nav.XamlRoot };
        M3Dialog.Style(d);
        await d.ShowAsync();
    }
}

internal static class WindowActivatorShim
{
    public static void Foreground(IntPtr hwnd) => FloatingDock.Windows.WindowManagement.WindowStyler.BringToFront(hwnd);
}
