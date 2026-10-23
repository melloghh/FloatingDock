using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Shell;
using FloatingDock.UI.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace FloatingDock.UI.Settings;

internal sealed partial class SettingsWindow
{
    private static StackPanel Page(string title, params UIElement[] parts)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Title(title));
        foreach (var x in parts) p.Children.Add(x);
        return p;
    }

    private DockSettings S => _app.Settings.Current;

    private UIElement MenuBarPage() => Page("Menu Bar",
        Ui.Toggle("Show the top menu bar", S.TopBarEnabled, (d, v) => d.TopBarEnabled = v),
        Ui.Toggle("Auto-hide — reveal at the top edge", S.TopBarAutoHide, (d, v) => d.TopBarAutoHide = v),
        Ui.Toggle("Keep maximized windows below the menu bar", S.TopBarReserveSpace, (d, v) => d.TopBarReserveSpace = v),
        Ui.Toggle("Show date next to the clock", S.TopBarShowDate, (d, v) => d.TopBarShowDate = v),
        Ui.Toggle("Show media controls",S.TopBarMediaEnabled,(d,v)=>d.TopBarMediaEnabled=v),
        Ui.Toggle("Show system tray arrow",S.TopBarTrayEnabled,(d,v)=>d.TopBarTrayEnabled=v),
        Ui.Toggle("Show search button",S.TopBarSearchEnabled,(d,v)=>d.TopBarSearchEnabled=v),
        Ui.Section("Motion",
            Ui.Slider("Hide animation frame limit",30,144,S.TopBarAnimationFps,(d,v)=>d.TopBarAnimationFps=v," fps"),
            Ui.Slider("Show duration",80,800,S.TopBarShowMs,(d,v)=>d.TopBarShowMs=v," ms"),
            Ui.Slider("Hide duration",80,800,S.TopBarHideMs,(d,v)=>d.TopBarHideMs=v," ms"),
            Ui.Slider("Hide delay",100,3000,S.TopBarHideDelayMs,(d,v)=>d.TopBarHideDelayMs=v," ms")),
        Ui.Toggle("Show seconds", S.TopBarShowSeconds, (d, v) => d.TopBarShowSeconds = v),
        Ui.Note("Current app on the left; network, sound, input language and clock on the right. Hidden during fullscreen and screen capture. Follows the Dock monitor setting."));

    // ------------------------------------------------------------------ Dock

    private UIElement DockPage()
    {
        var s = S;
        return Page("Dock",
            Ui.Section("Size & spacing",
                Ui.Slider("Icon size", 24, 128, s.IconSize, (d, v) => d.IconSize = v, " px"),
                Ui.Slider("Icon spacing", 0, 40, s.IconSpacing, (d, v) => d.IconSpacing = v, " px"),
                Ui.Slider("Dock height", 28, 200, s.DockHeight, (d, v) => d.DockHeight = v, " px")),
            Ui.Section("Position",
                Ui.Combo("Dock position", new[] { ("Bottom", DockEdge.Bottom), ("Left", DockEdge.Left), ("Right", DockEdge.Right) }, s.Edge, (d,v) => d.Edge=v),
                Ui.Slider("Distance from the screen edge", 0, 200, s.BottomOffset, (d, v) => d.BottomOffset = v, " px"),
                Ui.Advanced(Ui.Combo("Width", new[] { ("Fit the icons", WidthMode.Auto), ("Fixed width", WidthMode.Fixed) }, s.WidthMode, (d, v) => d.WidthMode = v),
                Ui.Slider("Fixed width", 200, 3000, s.FixedWidth, (d, v) => d.FixedWidth = v, " px"),
                Ui.Combo("Alignment", new[] { ("Center", DockAlignment.Center), ("Left", DockAlignment.Left), ("Right", DockAlignment.Right) }, s.Alignment, (d, v) => d.Alignment = v),
                Ui.Slider("Offset along the edge", -1500, 1500, s.HorizontalOffset, (d, v) => d.HorizontalOffset = v, " px"),
                Ui.Combo("Monitors", new[] { ("Primary monitor only", MonitorMode.Primary), ("Every monitor", MonitorMode.All) }, s.MonitorMode, (d, v) => d.MonitorMode = v))),
            PinnedSection());
    }

    private UIElement PinnedSection()
    {
        var list = new StackPanel { Spacing = 4 };
        void Fill()
        {
            list.Children.Clear();
            var pinned = _app.Pinned.Snapshot().Where(app=>!app.IsSeparator).ToList();
            if (pinned.Count == 0) list.Children.Add(Ui.Note("Nothing is pinned yet."));
            for (int i = 0; i < pinned.Count; i++)
            {
                var app = pinned[i];
                int index = i;
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int c = 0; c < 3; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                void Add(int col, string glyph, string tip, Action a, bool enabled = true)
                {
                    var b = new Button { Content = glyph, Padding = new Thickness(10, 3, 10, 3), IsEnabled = enabled };
                    ToolTipService.SetToolTip(b, L.Text(tip));
                    b.Click += (_, _) => a();
                    Grid.SetColumn(b, col);
                    row.Children.Add(b);
                }
                Add(1, "▲", "Move left", () => _app.Pinned.MoveApplication(app.Key, index - 1), index > 0);
                Add(2, "▼", "Move right", () => _app.Pinned.MoveApplication(app.Key, index + 1), index < pinned.Count - 1);
                Add(3, "✕", "Unpin", () => _app.Pinned.Remove(app.Key));
                list.Children.Add(row);
            }
        }
        Fill();
        void Changed() => _app.UiQueue.TryEnqueue(Fill);
        _app.Pinned.Changed += Changed;
        _pageCleanup.Add(() => _app.Pinned.Changed -= Changed);

        var add = Ui.Action("Add app…", async () =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            var r = await _app.Worker.Run(() => UserTileImporter.ImportAndSave(f.Path, _app.Paths.AppLibraryShortcutsDir, _app.Pinned));
            if (r.Status != TileImportStatus.Added) await Message("Pinned apps", r.Message ?? (r.Status == TileImportStatus.Duplicate ? "Already pinned." : "This file type is not supported."));
        });
        var import = Ui.Action("Import pins from the Windows taskbar", async () =>
        {
            var pins = await _app.Worker.Run(AppCatalog.ReadWindowsTaskbarPins);
            int n = 0;
            foreach (var p in pins) if (_app.Pinned.Add(p)) n++;
            await Message("Pinned apps", n > 0 ? L.Format("Imported {0} app(s).",n) : "No new pinned apps were found on the Windows taskbar.");
        });
        return Ui.Section("Pinned apps", list, Ui.Row(add, import), Ui.Note("Drag icons in the Dock to reorder them, drag one out of the Dock to unpin it."));
    }

    // ------------------------------------------------------------------ Appearance

    private UIElement AppearancePage()
    {
        var s = S;
        var colorBox = Ui.TextField("Background color (hex RGB)", s.BackgroundColor,
            t => ColorUtil.TryParseRgb(t, out _) ? null : "Use 6 hex digits, e.g. 1E1E1E", (d, v) => d.BackgroundColor = v.TrimStart('#').ToUpperInvariant());
        var pickerBtn = new Button { Content = L.Text("Pick color…") };
        pickerBtn.Click += (_, _) =>
        {
            ColorUtil.TryParseRgb(S.BackgroundColor, out var rgb);
            var picker = new ColorPicker { Color = global::Windows.UI.Color.FromArgb(255, rgb.R, rgb.G, rgb.B), IsAlphaEnabled = false, IsHexInputVisible = true };
            picker.ColorChanged += (_, e) => _app.Settings.Update(d => d.BackgroundColor = $"{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}");
            var fly = new Flyout { Content = picker };
            fly.Closed += (_, _) => Refresh();
            fly.ShowAt(pickerBtn);
        };
        return Page("Appearance",
            Ui.Combo("Interface language",new[]{("English",InterfaceLanguage.English),("Russian",InterfaceLanguage.Russian)},s.Language,(d,v)=>d.Language=v),
            Ui.Section("Background",
                Ui.Row(colorBox, new Border { VerticalAlignment = VerticalAlignment.Bottom, Child = pickerBtn }),
                Ui.Slider("Opacity", 0, 100, s.Opacity, (d, v) => d.Opacity = v, " %"),
                Ui.Slider("Corner radius", 0, 60, s.CornerRadius, (d, v) => d.CornerRadius = v, " px"),
                Ui.Toggle("Drop shadow", s.Shadow, (d, v) => d.Shadow = v),
                Ui.Toggle("Thin border", s.Border, (d, v) => d.Border = v),
                Ui.Toggle("Animate menus",s.MenuAnimations,(d,v)=>d.MenuAnimations=v),
                Ui.Slider("Menu animation duration",80,600,s.MenuAnimationMs,(d,v)=>d.MenuAnimationMs=v," ms")),
            Ui.Section("Theme & indicators",
                Ui.Combo("Theme (settings, Launchpad menus)", new[] { ("Dark", ThemeMode.Dark), ("Light", ThemeMode.Light), ("Follow system", ThemeMode.System) }, s.Theme, (d, v) => d.Theme = v),
                Ui.Combo("Running indicator", new[] { ("Dot", IndicatorStyle.Dot), ("Bar", IndicatorStyle.Bar) }, s.IndicatorStyle, (d, v) => d.IndicatorStyle = v),
                Ui.Toggle("Show app names above icons", s.ShowLabels, (d, v) => d.ShowLabels = v)));
    }

    // ------------------------------------------------------------------ Magnification

    private UIElement MagnificationPage()
    {
        var s = S;
        return Page("Magnification",
            Ui.Toggle("Magnify icons under the cursor", s.MagnificationEnabled, (d, v) => d.MagnificationEnabled = v),
            Ui.Slider("Maximum size", 100, 300, s.MaxScalePercent, (d, v) => d.MaxScalePercent = v, " %"),
            Ui.Slider("Reach around the cursor", 40, 500, s.MagnificationRadius, (d, v) => d.MagnificationRadius = v, " px"),
            Ui.Section("Motion",
                Ui.Slider("Animation frame limit", 30, 144, s.AnimationFps, (d, v) => d.AnimationFps = v, " fps"),
                Ui.Note("60 fps reduces CPU usage. Higher values follow high-refresh displays more closely."),
                Ui.Slider("Responsiveness (follow the cursor)", 1, 10, s.Responsiveness, (d, v) => d.Responsiveness = v),
                Ui.Slider("Animation speed (zoom in / out, auto-hide)", 1, 10, s.AnimationSpeed, (d, v) => d.AnimationSpeed = v),
                Ui.Slider("Smoothness (easing)", 0, 10, s.Smoothness, (d, v) => d.Smoothness = v),
                Ui.Toggle("Animate icons appearing / disappearing / reordering", s.IconAnimation, (d, v) => d.IconAnimation = v),
                Ui.Slider("Appear / disappear duration", 80, 800, s.IconAnimationMs, (d, v) => d.IconAnimationMs = v, " ms")));
    }

    // ------------------------------------------------------------------ Behaviour

    private UIElement BehaviourPage()
    {
        var s = S;
        var focusStatus = Ui.Note("");
        var focusField = Ui.TextField("Keyboard focus hotkey (arrows, Enter, Esc inside the dock)", s.DockFocusHotkey, Ui.HotkeyError, (d, v) => d.DockFocusHotkey = v, "Ctrl+Alt+D");
        focusField.Children.Add(focusStatus);
        HookHotkeyStatus(focusField, focusStatus, DockApp.HotkeyDockFocus);
        return Page("Behaviour",
            Ui.Section("What the dock shows",
                Ui.Toggle("Start button", s.ShowStartButton, (d, v) => d.ShowStartButton = v),
                Ui.Toggle("Launchpad button", s.ShowLaunchpadButton, (d, v) => d.ShowLaunchpadButton = v),
                Ui.Toggle("Pinned apps", s.ShowPinned, (d, v) => d.ShowPinned = v),
                Ui.Toggle("Running apps that are not pinned", s.ShowRunning, (d, v) => d.ShowRunning = v),
                Ui.Toggle("Highlight the active app", s.ShowActiveIndicator, (d, v) => d.ShowActiveIndicator = v)),
            Ui.Section("Hiding",
                Ui.Toggle("Auto-hide the dock", s.AutoHide, (d, v) => d.AutoHide = v),
                Ui.Toggle("Reveal when the cursor touches the screen edge", s.RevealOnEdgeHover, (d, v) => d.RevealOnEdgeHover = v),
                Ui.Toggle("Hide for fullscreen or maximized apps", s.HideOnFullscreen, (d, v) => d.HideOnFullscreen = v)),
            Ui.Section("Window previews",Ui.Toggle("Show window previews on hover",s.ShowWindowPreviews,(d,v)=>d.ShowWindowPreviews=v),Ui.Slider("Preview delay",200,2000,s.PreviewDelayMs,(d,v)=>d.PreviewDelayMs=v," ms")),
            Ui.Section("Clicks & startup",
                Ui.Toggle("Clicking the active app minimizes it", s.MinimizeOnRepeatClick, (d, v) => d.MinimizeOnRepeatClick = v),
                Ui.Toggle("Start FloatingDock with Windows", s.StartWithWindows, (d, v) => d.StartWithWindows = v),
                focusField));
    }

    private void HookHotkeyStatus(StackPanel field, TextBlock status, int hotkeyId)
    {
        bool active=true;
        void Update()
        {
            if(!active||_closed)return;
            status.Text = _app.HotkeyRegistered.TryGetValue(hotkeyId, out var ok) && !ok && FieldHasText(field) ? L.Text("This shortcut is already used by another program.") : "";
            status.Foreground = new SolidColorBrush(Colors.OrangeRed);
        }
        async void OnChanged(DockSettings _) { await Task.Delay(250); _app.UiQueue.TryEnqueue(Update); }
        _app.Settings.Changed += OnChanged;
        _pageCleanup.Add(() => {active=false;_app.Settings.Changed -= OnChanged;});
        Update();
    }

    private static bool FieldHasText(StackPanel field) => field.Children.OfType<TextBox>().FirstOrDefault()?.Text.Trim().Length > 0;

    // ------------------------------------------------------------------ Launchpad

    private UIElement LaunchpadPage()
    {
        var s = S;
        var status = Ui.Note("");
        var hotkey = Ui.TextField("Open / close Launchpad", s.LaunchpadHotkey, Ui.HotkeyError, (d, v) => d.LaunchpadHotkey = v, "Ctrl+Alt+Space");
        hotkey.Children.Add(status);
        HookHotkeyStatus(hotkey, status, DockApp.HotkeyLaunchpad);

        var tiles = new StackPanel { Spacing = 4 };
        void Fill()
        {
            tiles.Children.Clear();
            var list = _app.UserTiles.Snapshot();
            if (list.Count == 0) tiles.Children.Add(Ui.Note("No tiles added yet. Drag a .lnk or .exe file onto the open Launchpad."));
            foreach (var app in list)
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center });
                var b = new Button { Content = L.Text("Remove") };
                var key = app.Key;
                b.Click += (_, _) => _app.UserTiles.Remove(key);
                Grid.SetColumn(b, 1);
                row.Children.Add(b);
                tiles.Children.Add(row);
            }
        }
        Fill();
        void Changed() => _app.UiQueue.TryEnqueue(Fill);
        _app.UserTiles.Changed += Changed;
        _pageCleanup.Add(() => _app.UserTiles.Changed -= Changed);

        return Page("Launchpad",
            Ui.Combo("Launchpad icon",new[]{("White squares",LaunchpadIconStyle.WhiteSquares),("Color on dark",LaunchpadIconStyle.ColorOnDark),("Color on white",LaunchpadIconStyle.ColorOnWhite)},s.LaunchpadIconStyle,(d,v)=>{d.LaunchpadIconStyle=v;_app.TryResetIcon(Dock.SpecialApps.Launchpad);}),
            Ui.Section("Tiles",
                Ui.Slider("Icon size", 32, 160, s.LaunchpadIconSize, (d, v) => d.LaunchpadIconSize = v, " px"),
                Ui.Slider("Spacing", 4, 120, s.LaunchpadSpacing, (d, v) => d.LaunchpadSpacing = v, " px")),
            Ui.Section("Shortcut", hotkey),
            Ui.Section("Tiles you added", tiles, Ui.Note("Launchpad shows apps pinned in Windows Start, in their order, plus tiles you add manually. Refresh from Launchpad after changing Start pins.")));
    }

    // ------------------------------------------------------------------ Windows taskbar

    private UIElement TaskbarPage()
    {
        var s = S;
        var status = Ui.Note("");
        var hotkey = Ui.TextField("Emergency restore hotkey", s.RestoreTaskbarHotkey, Ui.HotkeyError, (d, v) => d.RestoreTaskbarHotkey = v, "Ctrl+Alt+Shift+T");
        hotkey.Children.Add(status);
        HookHotkeyStatus(hotkey, status, DockApp.HotkeyRestoreTaskbar);
        return Page("Windows Taskbar",
            Ui.Note("FloatingDock replaces the taskbar visually. The original Windows taskbar is always put back when FloatingDock exits, crashes, or when you use any of the restore options below."),
            Ui.Combo("Windows taskbar", new[]
                {
                    ("Leave as it is", TaskbarMode.Leave),
                    ("Auto-hide", TaskbarMode.AutoHide),
                    ("Hide completely (experimental, also hides tray and clock)", TaskbarMode.HideCompletely),
                }, s.TaskbarMode, (d, v) => d.TaskbarMode = v),
            Ui.Toggle("Reserve screen space for the dock (windows maximize above it)", s.ReserveWorkArea, (d, v) => d.ReserveWorkArea = v),
            Ui.Note("Space is only reserved while the taskbar is auto-hidden / hidden and the dock does not auto-hide."),
            Ui.Section("Restore", Ui.Action("Restore the Windows taskbar now", () => { _app.RestoreTaskbarNow(); Refresh(); }), hotkey,
                Ui.Note("If everything fails, start  FloatingDock.exe --restore-taskbar  or use Settings → Personalization → Taskbar → Automatically hide.")));
    }

    // ------------------------------------------------------------------ About

    private UIElement AboutPage()
    {
        var version = typeof(DockApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return Page("About",
            new TextBlock { Text = $"FloatingDock {version}", FontSize = 18 },
            Ui.Note("A floating, macOS-style dock, Launchpad and icon packs for Windows 10 / 11. MIT license."),
            Ui.Section("Data",
                Ui.Note(_app.Paths.Root),
                Ui.Row(Ui.Action("Open data folder", () => ShellLauncher.OpenFolder(_app.Paths.Root)),
                       Ui.Action("Open log folder", () => ShellLauncher.OpenFolder(_app.Paths.LogsDir)))),
            Ui.Section("Maintenance",
                Ui.Action("Reset all settings to defaults", async () =>
                {
                    var r = await Dialog("Reset settings", new TextBlock { Text = L.Text("All dock settings return to their defaults. Pinned apps, Launchpad tiles and icons are kept.") }, "Reset");
                    if (r != ContentDialogResult.Primary) return;
                    var defaults = new DockSettings();
                    _app.Settings.Update(d =>
                    {
                        foreach (var p in typeof(DockSettings).GetProperties().Where(p => p.CanWrite)) p.SetValue(d, p.GetValue(defaults));
                    });
                    Refresh();
                }),
                Ui.Action("Restart FloatingDock", () => _app.Restart()),
                Ui.Action("Quit FloatingDock", () => _app.Shutdown())));
    }
}
