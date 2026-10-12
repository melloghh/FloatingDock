using System.Numerics;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace FloatingDock.UI.Launchpad;

/// <summary>
/// Fullscreen library of Windows Start pins and tiles explicitly added by the user.
/// The window is created once and then only shown / hidden. Tiles are virtualised (ItemsRepeater) and pooled.
/// </summary>
internal sealed class LaunchpadWindow : IDisposable
{
    private readonly DockApp _app = DockApp.Current;
    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly Grid _root = new() { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(92, 8, 10, 16)), AllowDrop = true };
    private readonly Grid _content = new();
    private readonly Border _shell=new() { CornerRadius=new CornerRadius(28),Padding=new Thickness(28),Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(164,18,21,27)),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(global::Windows.UI.Color.FromArgb(26,255,255,255)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBox _search = new();
    private readonly Button _focusSink = new() { Width = 1, Height = 1, Opacity = 0, IsTabStop = false, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _settingsButton = new() { Content = new SymbolIcon(Symbol.Setting), Width=40,Height=40,CornerRadius=new CornerRadius(12),Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(12,255,255,255)),BorderThickness=new Thickness(0) };
    private readonly Button _refreshButton = new() { Content = new SymbolIcon(Symbol.Refresh), Width=40,Height=40,CornerRadius=new CornerRadius(12),Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(12,255,255,255)),BorderThickness=new Thickness(0) };
    private readonly Visual _overlayVisual;
    private readonly ItemsRepeater _repeater = new();
    private readonly UniformGridLayout _layout = new() { ItemsJustification = UniformGridLayoutItemsJustification.Center, ItemsStretch = UniformGridLayoutItemsStretch.None };
    private readonly TextBlock _empty = new() { Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(200, 255, 255, 255)), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _toast = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly TextBlock _toastText = new() { Foreground = new SolidColorBrush(Colors.White), FontSize = 14 };
    private readonly Visual _contentVisual;
    private readonly AppSearchIndex _index = new(Array.Empty<AppRef>());
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly DispatcherQueueTimer _transitionTimer;
    private readonly GlassBackdrop _glass=new();
    private int _allVersion, _viewVersion = -1;
    private string? _lastViewQuery;

    private List<AppRef>? _catalog;
    private DateTime _catalogLoaded = DateTime.MinValue;
    private List<AppRef> _all = new();
    private List<AppRef> _view = new();
    private bool _visible, _closing, _loadingCatalog, _disposed;
    private int _modal;
    private int _visibilityGeneration;
    private int _backgroundPressGeneration = -1;
    private int _animatedGeneration = -1;
    private bool _opening;
    private Task? _preparation;
    private double _scale = 1.0;
    private DockSettings _s;

    public LaunchpadWindow()
    {
        _s = _app.Settings.Current;
        _searchTimer = _app.UiQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(75);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => RebuildView();
        _transitionTimer=_app.UiQueue.CreateTimer();_transitionTimer.IsRepeating=false;
        _transitionTimer.Tick+=(_,_)=> { if(_closing)FinishHide();else FinishOpening(); };
        BuildTree();
        _window = new Window { SystemBackdrop = _glass, Content = _root, Title = "FloatingDock Launchpad" };
        WindowSetup.MakeBorderless(_window);
        _hwnd = WindowSetup.Hwnd(_window);
        WindowStyler.MakeOverlayWindow(_hwnd, noActivate: false);
        _contentVisual = ElementCompositionPreview.GetElementVisual(_content);
        _overlayVisual = ElementCompositionPreview.GetElementVisual(_root);
        _root.SizeChanged += (_, _) => _overlayVisual.CenterPoint = new Vector3((float)_root.ActualWidth / 2, (float)_root.ActualHeight / 2, 0);
        _toastTimer = _app.UiQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Opacity = 0; };

        _window.Activated += OnActivated;
        _app.UserTiles.Changed += OnTilesChanged;
        _app.Settings.Changed += OnSettings;
        _app.Icons.IconsChanged += OnIconsChanged;
        _root.KeyDown += OnKeyDown;
        _root.CharacterReceived += OnCharacterReceived;
        _root.Tapped += OnBackgroundTapped;
        _root.PointerPressed += (_, _) => { if (_visible && !_closing) _backgroundPressGeneration = _visibilityGeneration; };
        _root.DragOver += OnDragOver;
        _root.Drop += OnDrop;
        _root.Loaded += (_, _) => { if (_visible && _opening) BeginOpening(_visibilityGeneration); };
        _settingsButton.Click += (_, _) => OpenSettings();
        _refreshButton.Click += async (_, _) => await RefreshCatalogAsync(force: true);
    }

    private void BuildTree()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _search.PlaceholderText = "Поиск приложений";
        _search.MinWidth=120;_search.MaxWidth=260;
        _search.Height=34;_search.MinHeight=34;_search.VerticalAlignment=VerticalAlignment.Center;
        _search.Padding=new Thickness(12,5,12,5);
        _search.FontSize = 13;
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        _search.Margin = new Thickness(24,0,24,0);
        _search.Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(16,255,255,255));
        _search.BorderThickness=new Thickness(0);
        _search.CornerRadius = new CornerRadius(11);
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _search.Tapped += (_, e) => e.Handled = true;
        var header=new Grid { Height=48,Margin=new Thickness(0,0,0,20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });header.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });header.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var heading=new StackPanel { VerticalAlignment=VerticalAlignment.Center,Spacing=4 };
        heading.Children.Add(new TextBlock { Text="Приложения",FontSize=23,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Foreground=new SolidColorBrush(Colors.White) });
        heading.Children.Add(new TextBlock { Text="Закреплено в меню «Пуск»",FontSize=12,Foreground=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,169,177,192)) });
        header.Children.Add(heading);Grid.SetColumn(_search,1);header.Children.Add(_search);
        var actions=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center };
        actions.Children.Add(_refreshButton);actions.Children.Add(_settingsButton);Grid.SetColumn(actions,2);header.Children.Add(actions);_content.Children.Add(header);

        _repeater.Layout = _layout;
        _repeater.Background = new SolidColorBrush(Colors.Transparent);
        _repeater.ItemTemplate = new TileFactory(() => TileMetrics(), t =>
        {
            t.Tapped += OnTileTapped;
            t.RightTapped += OnTileRightTapped;
        });
        var scroll = new ScrollViewer
        {
            Content = _repeater, Background = new SolidColorBrush(Colors.Transparent), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 0, 40), Margin = new Thickness(0, 0, 0, 0),
        };
        Grid.SetRow(scroll, 1);
        _content.Children.Add(scroll);
        _content.Children.Add(_empty);
        Grid.SetRow(_empty, 1);

        _toast.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(235, 36, 38, 46));
        _toast.CornerRadius = new CornerRadius(10);
        _toast.Padding = new Thickness(16, 9, 16, 10);
        _toast.Child = _toastText;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 36);
        _toast.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };

        Grid.SetRowSpan(_shell,2);_shell.Child=_content;
        _root.Children.Add(_shell);
        _root.Children.Add(_toast);
        Grid.SetRowSpan(_toast, 2);

        Grid.SetRowSpan(_focusSink, 2); _root.Children.Add(_focusSink);
        ToolTipService.SetToolTip(_settingsButton, "FloatingDock settings");
        ToolTipService.SetToolTip(_refreshButton, "Refresh Windows Start pins");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_settingsButton, "FloatingDock settings");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_refreshButton, "Refresh Windows Start pins");
        _settingsButton.Tapped += (_, e) => e.Handled = true;
        _refreshButton.Tapped += (_, e) => e.Handled = true;
    }

    private (int Icon, double Width, double Scale) TileMetrics()
    {
        int icon = _s.LaunchpadIconSize;
        return (icon, Math.Max(icon + 28, 96), _scale);
    }

    private void ApplyLayoutSettings()
    {
        _s = _app.Settings.Current;
        var (icon, width, _) = TileMetrics();
        _layout.MinItemWidth = width;
        _layout.MinItemHeight = icon + 8 + 10 + 38;
        _layout.MinColumnSpacing = _s.LaunchpadSpacing;
        _layout.MinRowSpacing = _s.LaunchpadSpacing;
        WindowSetup.ApplyTheme(_root, ThemeMode.Dark);
    }

    private void SetPanelBounds(MonitorDescriptor monitor)
    {
        _shell.Width=Math.Max(320,Math.Min(1440,monitor.Bounds.Width/_scale-48));
        _shell.Height=Math.Max(260,Math.Min(940,monitor.Bounds.Height/_scale-72));
    }

    // ------------------------------------------------------------------ show / hide

    public void Toggle()
    {
        if (_visible && !_closing) Hide(); else Show();
    }

    internal IntPtr Handle => _hwnd;
    internal int CatalogCount => _catalog?.Count ?? 0;
    internal bool IsCatalogLoaded => _catalog is not null;
    internal Task LoadCatalogForSmokeAsync() => RefreshCatalogAsync(force: true);
    internal bool IsOpen => _visible && !_closing;
    internal string DiagnosticState => $"visible={_visible}, closing={_closing}, opening={_opening}, nativeVisible={WindowStyler.IsVisible(_hwnd)}, live={FloatingDock.Windows.Native.NativeMethods.IsWindow(_hwnd)}, hwnd={_hwnd}, owner={FloatingDock.Windows.Native.NativeMethods.GetWindow(_hwnd, 4)}, foreground={WindowStyler.Foreground()}";
    internal int RealizedTileCount => Tiles().Count();
    internal IReadOnlyList<LaunchpadTile> TilesForDiagnostics=>Tiles().ToArray();
    internal int ViewRebuilds { get; private set; }
    internal (double Width,double Height) SearchSize => (_search.ActualWidth,_search.ActualHeight);
    internal float ContentOpacity => _contentVisual.Opacity;
    internal bool HasSettingsShortcut => _settingsButton.IsEnabled;
    internal bool IsPrepared => _preparation?.IsCompletedSuccessfully == true;
    internal int ExportedStartPins { get; private set; }
    internal string SearchSampleName => _all.FirstOrDefault()?.Name ?? "";
    internal async Task<int> SearchForSmokeAsync(string query)
    {
        _search.Text = query;
        // TextChanged is queued by WinUI; inspect after the UI dispatcher processes it.
        await Task.Delay(150);
        return _view.Count;
    }

    public void Show()
    {
        if (_disposed || (_visible && !_closing)) return;
        bool reversing = _visible;
        var mon = MonitorHelper.FromCursor() ?? MonitorHelper.Primary();
        if (mon is null) return;
        _scale = mon.Scale;
        _visibilityGeneration++;
        _transitionTimer.Stop();
        _backgroundPressGeneration = -1;
        _opening = true;
        _closing = false;
        ApplyLayoutSettings();
        WindowStyler.SetBounds(_hwnd, mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Width, mon.Bounds.Height);
        SetPanelBounds(mon);

        _search.Text = "";
        if (_catalog is null && _allVersion==0) RebuildAll();
        else RebuildView();
        foreach (var tile in Tiles()) tile.Bind(tile.App!, _s.LaunchpadIconSize, TileMetrics().Width, _scale);

        _visible = true;
        _app.Dock.SetSuspended(true);
        StopTransition();
        _root.Opacity = 1;
        if (!reversing) { _overlayVisual.Opacity = 0; _overlayVisual.Scale = new Vector3(1.025f, 1.025f, 1); }
        WindowStyler.SetRegion(_hwnd, null);
        WindowStyler.SetNoActivate(_hwnd, false);
        _glass.SetVisible(true);_window.AppWindow.Show();
        _window.Activate();
        WindowStyler.Show(_hwnd, true);
        WindowStyler.BringToFront(_hwnd);
        _focusSink.Focus(FocusState.Programmatic);
        if (_root.IsLoaded) BeginOpening(_visibilityGeneration);
        _ = RefreshCatalogAsync(force: _catalog is null || DateTime.UtcNow - _catalogLoaded > TimeSpan.FromMinutes(2), refreshSource: false);
    }

    /// <summary>Realizes the first page once, invisibly and without activating a window, before the first opening animation.</summary>
    internal Task PrepareAsync() => _preparation ??= PrepareCoreAsync();

    private async Task PrepareCoreAsync()
    {
        await RefreshCatalogAsync(force: true, refreshSource: false);
        if (_disposed || _visible) return;
        var mon = MonitorHelper.Primary();
        if (mon is null) return;
        _scale = mon.Scale;
        await Task.WhenAll(_all.Take(48).Select(a => _app.Icons.GetAsync(a, (int)Math.Ceiling(_s.LaunchpadIconSize * _scale))));
        if (_disposed || _visible) return;
        ApplyLayoutSettings();
        SetPanelBounds(mon);
        _root.Opacity = 1;
        _overlayVisual.Opacity = 0;
        WindowStyler.SetNoActivate(_hwnd, true);
        WindowStyler.SetBounds(_hwnd, mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Width, mon.Bounds.Height);
        WindowStyler.SetRegion(_hwnd, (0, 0, 0, 0));
        try
        {
            _window.AppWindow.Show(false);
            await Task.Delay(100);
            if (!_disposed && !_visible)
            {
                _root.UpdateLayout();
                // Render the hidden page once to prepare its GPU image resources before the first visible animation.
                var snapshot = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                // Warm tile textures with a bounded target, rather than allocating a full-monitor bitmap.
                try { await snapshot.RenderAsync(_repeater, Math.Min(512, Math.Max(1, (int)_repeater.ActualWidth)), Math.Min(384, Math.Max(1, (int)_repeater.ActualHeight))); }
                catch (Exception ex) { _app.Log.Warn("Optional Launchpad GPU preparation skipped: " + ex.Message); }
            }
            await Task.Delay(100);
        }
        finally
        {
            if (!_disposed)
            {
                WindowStyler.SetRegion(_hwnd, null);
                WindowStyler.SetNoActivate(_hwnd, false);
                if (!_visible) { WindowStyler.Hide(_hwnd); _overlayVisual.Opacity = 0; }
            }
        }
    }

    private void BeginOpening(int generation)
    {
        if (generation != _visibilityGeneration || !_visible || _closing || _animatedGeneration == generation) return;
        _animatedGeneration = generation;
        AnimateOverlay(0, 1, 1.025, 1, 280);
        _glass.AnimateOpacity(1,280,entering:true);
        _transitionTimer.Interval=TimeSpan.FromMilliseconds(Math.Max(1,SurfaceMotion.Duration(310)));_transitionTimer.Start();
    }

    private void FinishOpening()
    {
        if (_disposed || !_visible || _closing) return;
        WindowStyler.BringToFront(_hwnd);
        StopTransition();
        _overlayVisual.Opacity = 1;
        _overlayVisual.Scale = Vector3.One;
        _opening = false;
    }

    public void Hide()
    {
        if (!_visible || _closing) return;
        if (Program.IsSmokeTest) _app.Log.Info("Launchpad hide requested: " + new System.Diagnostics.StackTrace(1, false));
        _closing = true;
        _opening = false;
        ++_visibilityGeneration;_transitionTimer.Stop();
        AnimateOverlay(1, 0, 1, 1.015, 150);
        _glass.AnimateOpacity(0,150);
        _transitionTimer.Interval=TimeSpan.FromMilliseconds(Math.Max(1,SurfaceMotion.Duration(180)));_transitionTimer.Start();
    }

    private void FinishHide()
    {
        if (!_closing) return;
        _closing = false;
        _visible = false;
        _window.AppWindow.Hide();
        WindowStyler.Hide(_hwnd);
        _glass.SetVisible(false);StopTransition();
        _searchTimer.Stop();
        _app.Dock.SetSuspended(false);
    }

    private void StopTransition()
    {
        _overlayVisual.StopAnimation("Opacity");
        _overlayVisual.StopAnimation("Scale");
    }

    private void AnimateOverlay(double fromOpacity, double toOpacity, double fromScale, double toScale, int milliseconds)
    {
        SurfaceMotion.Animate(_overlayVisual,(float)toOpacity,(float)toScale,milliseconds);
    }

    internal void OpenSettings()
    {
        Hide();
        _app.ShowSettings("Launchpad");
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (Program.IsSmokeTest) _app.Log.Info($"Launchpad activation {e.WindowActivationState}, opening={_opening}, visible={_visible}, foreground={WindowStyler.Foreground()}, hwnd={_hwnd}");
        if (e.WindowActivationState == WindowActivationState.Deactivated && _visible && !_closing && !_opening && _modal == 0)
            _ = HideAfterStableDeactivationAsync(_visibilityGeneration);
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (!_visible || _closing || _modal > 0 || e.Character < 32 || e.Character == 127 || _root.XamlRoot is null || ReferenceEquals(FocusManager.GetFocusedElement(_root.XamlRoot), _search)) return;
        StartTypingSearch(char.ConvertFromUtf32((int)e.Character));
        e.Handled = true;
    }

    private void StartTypingSearch(string text)
    {
        _search.Focus(FocusState.Programmatic);
        _search.Text += text;
        _search.SelectionStart = _search.Text.Length;
    }

    internal bool TypeSearchForSmoke(string text)
    {
        StartTypingSearch(text);
        return _search.Text.EndsWith(text, StringComparison.Ordinal) && _search.FocusState != FocusState.Unfocused;
    }

    private async Task HideAfterStableDeactivationAsync(int generation)
    {
        await Task.Delay(200);
        if (_disposed || generation != _visibilityGeneration || !_visible || _closing || _opening || _modal > 0) return;
        if (WindowStyler.Foreground() != _hwnd)
        {
            _app.Log.Info("Launchpad dismissed after a stable foreground change.");
            Hide();
        }
    }

    internal void SimulateQueuedDeactivationForSmoke() => _ = HideAfterStableDeactivationAsync(_visibilityGeneration);

    // ------------------------------------------------------------------ data

    private async Task RefreshCatalogAsync(bool force, bool refreshSource = true)
    {
        if (_loadingCatalog || !force) return;
        _loadingCatalog = true;
        try
        {
            _refreshButton.IsEnabled = false;
            var result = await _app.StartPins.GetAsync(refreshSource);
            bool changed = !ReferenceEquals(_catalog, result.Apps);
            _catalog = result.Apps;
            ExportedStartPins = result.ExportedPins;
            _catalogLoaded = DateTime.UtcNow;
            if (!_disposed && changed) RebuildAll();
        }
        catch (Exception ex) { _app.Log.Error("Start pins load failed", ex); if (!_disposed) { _empty.Text = "Could not read Windows Start pins. Use Refresh or add a .lnk / .exe."; } }
        finally { _loadingCatalog = false; if (!_disposed) _refreshButton.IsEnabled = true; }
    }

    private void RebuildAll()
    {
        var user = _app.UserTiles.Snapshot();
        var userKeys = new HashSet<string>(user.Select(u => u.Key));
        var rest = (_catalog ?? new()).Where(a => !userKeys.Contains(a.Key)).ToList();
        _all = user.Concat(rest).ToList();
        _allVersion++;
        _index.Rebuild(_all);
        RebuildView();
    }

    private void RebuildView()
    {
        _searchTimer.Stop();
        var q = _search.Text;
        if (_viewVersion == _allVersion && _lastViewQuery == q) return;
        _viewVersion = _allVersion; _lastViewQuery = q;
        ViewRebuilds++;
        _view = string.IsNullOrWhiteSpace(q) ? _all : _index.Search(q).ToList();
        _repeater.ItemsSource = _view;
        _empty.Text = _view.Count > 0 ? "" : _catalog is null ? "Loading Windows Start pins…" : string.IsNullOrWhiteSpace(q) ? "Pin apps in Windows Start or drop a .lnk / .exe here" : $"No results for “{q}”";
    }

    private void OnTilesChanged() => _app.UiQueue.TryEnqueue(() => { if (!_disposed) RebuildAll(); });
    private void OnSettings(DockSettings s) => _app.UiQueue.TryEnqueue(() =>
    {
        bool layoutChanged = _s.LaunchpadIconSize != s.LaunchpadIconSize || _s.LaunchpadSpacing != s.LaunchpadSpacing;
        _s = s;
        if (_visible && layoutChanged)
        {
            ApplyLayoutSettings();
            foreach (var tile in Tiles()) tile.Bind(tile.App!, s.LaunchpadIconSize, TileMetrics().Width, _scale);
        }
    });
    private void OnIconsChanged() { LaunchpadTile.IconEpoch++;foreach (var t in Tiles()) t.ReloadIcon(); }

    private IEnumerable<LaunchpadTile> Tiles()
    {
        for (int i = 0; i < _view.Count; i++)
            if (_repeater.TryGetElement(i) is LaunchpadTile t) yield return t;
    }

    // ------------------------------------------------------------------ interaction

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Escape) { Hide(); e.Handled = true; }
        else if (e.Key == global::Windows.System.VirtualKey.Enter) { RebuildView(); if (_view.Count > 0) LaunchAndClose(_view[0]); e.Handled = true; }
    }

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.Handled) return;
        // A release/tap queued by the dock's opening click must not dismiss the newly shown overlay.
        if (_opening || _backgroundPressGeneration != _visibilityGeneration)
        {
            if (Program.IsSmokeTest) _app.Log.Info("Launchpad ignored a background tap without a current visible-window press.");
            return;
        }
        if (e.OriginalSource is DependencyObject d && (IsInside<LaunchpadTile>(d) || IsInside<Button>(d) || IsInside<TextBox>(d))) return;
        Hide();
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur)) if (cur is T) return true;
        return false;
    }

    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is LaunchpadTile { App: { } app }) LaunchAndClose(app);
    }

    private void LaunchAndClose(AppRef app)
    {
        _ = _app.Worker.Run(() => { if (!ShellLauncher.Launch(app)) _app.Log.Warn("launch failed: " + app.Name); });
        Hide();
    }

    private const int CmdOpen = 1, CmdPin = 2, CmdRemove = 3, CmdLeft = 4, CmdRight = 5, CmdIcon = 6, CmdResetIcon = 7, CmdFolder = 8;

    private async void OnTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not LaunchpadTile { App: { } app }) return;
        bool isUser = _app.UserTiles.Contains(app.Key);
        bool searching = !string.IsNullOrWhiteSpace(_search.Text);
        bool pinned = _app.Pinned.Contains(app.Key);
        var userKeys = _app.UserTiles.Snapshot().Select(a => a.Key).ToList();
        int ix = userKeys.IndexOf(app.Key);

        var items = new List<MenuItemSpec>
        {
            new(CmdOpen, "Open"),
            new(CmdPin, pinned ? "Unpin from Dock" : "Pin to Dock"),
        };
        if (isUser)
        {
            items.Add(new(CmdRemove, "Remove from Launchpad"));
            if (!searching)
            {
                items.Add(new(CmdLeft, "Move earlier", Disabled: ix <= 0));
                items.Add(new(CmdRight, "Move later", Disabled: ix >= userKeys.Count - 1));
            }
        }
        items.Add(MenuItemSpec.Separator);
        items.Add(new(CmdIcon, "Change icon…"));
        items.Add(new(CmdResetIcon, "Reset icon", Disabled: !_app.Overrides.Has(app)));
        items.Add(new(CmdFolder, "Open file location", Disabled: ShellLauncher.LocationOf(app) is null));

        _modal++;
        int cmd;
        try { cmd = NativeMenu.Show(_app.MessageHandle, items, bottomAlign: false); }
        finally { _modal--; }
        if (_visible && !_closing) WindowStyler.BringToFront(_hwnd);

        switch (cmd)
        {
            case CmdOpen: LaunchAndClose(app); break;
            case CmdPin: if (pinned) _app.Pinned.Remove(app.Key); else _app.Pinned.Add(app.Clone()); break;
            case CmdRemove: _app.UserTiles.Remove(app.Key); break;
            case CmdLeft: _app.UserTiles.Move(app.Key, ix - 1); break;
            case CmdRight: _app.UserTiles.Move(app.Key, ix + 1); break;
            case CmdIcon:
                _modal++;
                try { await WindowSetup.PickIconAsync(_hwnd, app); }
                finally { _modal--; if (_visible && !_closing) WindowStyler.BringToFront(_hwnd); }
                break;
            case CmdResetIcon: _app.TryResetIcon(app); break;
            case CmdFolder:
                var loc = ShellLauncher.LocationOf(app);
                if (loc is not null) { ShellLauncher.RevealInExplorer(loc); Hide(); }
                break;
        }
    }

    // ------------------------------------------------------------------ drag & drop of .lnk / .exe

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to Launchpad";
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else e.AcceptedOperation = DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            int added = 0, duplicate = 0, unsupported = 0;
            foreach (var path in paths)
            {
                var result = await _app.Worker.Run(() =>
                    UserTileImporter.Import(path, _app.Paths.AppLibraryShortcutsDir, key => _app.UserTiles.Contains(key)));
                switch (result.Status)
                {
                    case TileImportStatus.Added when result.App is not null: _app.UserTiles.Add(result.App); added++; break;
                    case TileImportStatus.Duplicate: duplicate++; break;
                    default: unsupported++; break;
                }
            }
            var parts = new List<string>();
            if (added > 0) parts.Add($"Added {added}");
            if (duplicate > 0) parts.Add($"{duplicate} already in Launchpad");
            if (unsupported > 0) parts.Add($"{unsupported} not supported (use .lnk or .exe)");
            ShowToast(parts.Count == 0 ? "Nothing to add" : string.Join(" · ", parts));
        }
        catch (Exception ex)
        {
            _app.Log.Error("drop failed", ex);
            ShowToast("Could not add the dropped item");
        }
        finally { deferral.Complete(); }
    }

    private void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transitionTimer.Stop();
        StopTransition();
        _toastTimer.Stop();
        _searchTimer.Stop();
        _app.UserTiles.Changed -= OnTilesChanged;
        _app.Settings.Changed -= OnSettings;
        _app.Icons.IconsChanged -= OnIconsChanged;
        try { _window.Close(); } catch { }
    }
}
