using FloatingDock.UI.Views;
using FloatingDock.Core.Configuration;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Settings;

/// <summary>
/// Code-only Material 3 control builders. Every control writes straight into <see cref="SettingsStore"/>.
/// Settings are laid out like the reference: a section label, then a group of list-item cards that are
/// 6 px apart, with large outer corners on the group and small inner corners between neighbours.
/// </summary>
internal static class Ui
{
    private const string RowTag = "m3row";
    private static SettingsStoreAccess S => new();
    internal readonly struct SettingsStoreAccess { public void Update(Action<DockSettings> a) => DockApp.Current.Settings.Update(a); }

    private static global::Windows.UI.Text.FontWeight Medium => new() { Weight = 500 };
    private static global::Windows.UI.Text.FontWeight SemiBold => Microsoft.UI.Text.FontWeights.SemiBold;

    // ------------------------------------------------------------------ text

    public static TextBlock Title(string text) => new()
    {
        Text = L.Text(text), FontSize = 30, FontWeight = Medium, Margin = new Thickness(4, 0, 0, 14),
        Foreground = M3.Text(),
    };

    public static TextBlock Note(string text) => new()
    {
        Text = L.Text(text), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(16, 2, 16, 2),
        Foreground = M3.Text(secondary: true),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = L.Text(text), FontSize = 14.5, FontWeight = Medium, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        Foreground = M3.Text(),
    };

    // ------------------------------------------------------------------ grouping

    private static bool IsRow(UIElement e) => e is ToggleSwitch or Expander || e is FrameworkElement { Tag: RowTag };

    private static void Round(UIElement row, CornerRadius radius)
    {
        switch (row)
        {
            case ToggleSwitch t: t.CornerRadius = radius; break;
            case Expander x: x.CornerRadius = radius; break;
            case Border b: b.CornerRadius = radius; break;
            case StackPanel p: p.CornerRadius = radius; break;
            case Grid g: g.CornerRadius = radius; break;
        }
    }

    private static Border RowCard(UIElement content) => new()
    {
        Child = content, Tag = RowTag, Padding = new Thickness(16, 10, 16, 10), MinHeight = 56,
        Background = M3.CardHigh(), CornerRadius = new CornerRadius(M3.CornerL),
    };

    /// <summary>
    /// Stacks <paramref name="children"/>; consecutive list-item rows become one group:
    /// 24 px outer corners, 6 px between neighbours, 3 px gaps.
    /// </summary>
    public static StackPanel Layout(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 3 };
        var run = new List<UIElement>();
        void Flush()
        {
            for (int i = 0; i < run.Count; i++)
            {
                double top = i == 0 ? 24 : 6, bottom = i == run.Count - 1 ? 24 : 6;
                Round(run[i], new CornerRadius(top, top, bottom, bottom));
            }
            run.Clear();
        }
        foreach (var raw in children)
        {
            if (raw is TextBlock { Text.Length: 0 }) continue; // an empty note (nothing to say) must not leave a gap
            var c = raw is ComboBox ? RowCard(raw) : raw;
            if (IsRow(c)) { run.Add(c); panel.Children.Add(c); continue; }
            Flush();
            if (c is Button or StackPanel { Orientation: Orientation.Horizontal } && panel.Children.Count > 0) panel.Children.Add(new Border { Height = 6 });
            panel.Children.Add(c);
        }
        Flush();
        return panel;
    }

    /// <summary>A labelled group: primary-coloured section label, then its list-item rows.</summary>
    public static StackPanel Section(string title, params UIElement[] children)
    {
        var p = new StackPanel { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        p.Children.Add(new TextBlock
        {
            Text = L.Text(title), FontSize = 13.5, FontWeight = SemiBold, Margin = new Thickness(16, 0, 0, 0),
            Foreground = M3.Brush(M3.Current.Primary),
        });
        p.Children.Add(Layout(children));
        return p;
    }

    // ------------------------------------------------------------------ controls

    public static ToggleSwitch Toggle(string header, bool value, Action<DockSettings, bool> set)
    {
        var t = new ToggleSwitch
        {
            Header = L.Text(header), IsOn = value, OnContent = null, OffContent = null,
            Style = (Style)Application.Current.Resources["M3SwitchStyle"],
            Background = M3.CardHigh(), Foreground = M3.Text(),
            Padding = new Thickness(16, 8, 16, 8), CornerRadius = new CornerRadius(M3.CornerL),
        };
        t.Toggled += (_, _) => S.Update(s => set(s, t.IsOn));
        return t;
    }

    public static UIElement Slider(string header, int min, int max, int value, Action<DockSettings, int> set, string suffix = "")
    {
        var label = Label(header);
        var readout = new TextBlock { Text = $"{value}{suffix}", FontSize = 13.5, FontWeight = Medium, VerticalAlignment = VerticalAlignment.Center, Foreground = M3.Brush(M3.Current.Primary) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, StepFrequency = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, -2) };
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            readout.Text = $"{v}{suffix}";
            S.Update(s => set(s, v));
        };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(label);
        Grid.SetColumn(readout, 1);
        top.Children.Add(readout);
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(top);
        p.Children.Add(slider);
        return RowCard(p);
    }

    public static UIElement Combo<T>(string header, IReadOnlyList<(string Label, T Value)> items, T selected, Action<DockSettings, T> set) where T : notnull
    {
        var c = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(M3.CornerM) };
        foreach (var (label, _) in items) c.Items.Add(L.Text(label));
        c.SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(c, L.Text(header));
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedIndex < 0) return;
            var v = items[c.SelectedIndex].Value;
            S.Update(s => set(s, v));
        };
        return LabelledRow(header, c);
    }

    /// <summary>List item: label on the left, a control on the trailing edge.</summary>
    public static Border LabelledRow(string header, UIElement control)
    {
        var g = new Grid { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Label(header));
        Grid.SetColumn((FrameworkElement)control, 1);
        g.Children.Add(control);
        return RowCard(g);
    }

    /// <summary>Text box that commits on Enter / focus loss. <paramref name="validate"/> returns an error text or null.</summary>
    public static StackPanel TextField(string header, string value, Func<string, string?> validate, Action<DockSettings, string> set, string? placeholder = null)
    {
        var box = new TextBox
        {
            Header = L.Text(header), Text = value, PlaceholderText = L.Text(placeholder ?? ""), HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(M3.CornerM), MinHeight = 48, VerticalContentAlignment = VerticalAlignment.Center,
        };
        var error = new TextBlock { Foreground = M3.Brush(M3.Current.Error), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void Commit()
        {
            var text = box.Text.Trim();
            var err = validate(text);
            error.Text = L.Text(err ?? "");
            error.Visibility = err is null ? Visibility.Collapsed : Visibility.Visible;
            if (err is null) S.Update(s => set(s, text));
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == global::Windows.System.VirtualKey.Enter) Commit(); };
        // The panel is the list item itself, so callers can keep appending status text to it.
        var p = new StackPanel
        {
            Spacing = 6, Tag = RowTag, Padding = new Thickness(16, 12, 16, 12), Background = M3.CardHigh(),
            CornerRadius = new CornerRadius(M3.CornerL),
        };
        p.Children.Add(box);
        p.Children.Add(error);
        return p;
    }

    private static readonly (string Name, string Hex)[] Seeds =
    {
        ("Mint", "4E9F75"), ("Graphite", "5F6368"), ("Blue", "4A7BD0"), ("Teal", "2E8B7F"), ("Gold", "C9A227"),
        ("Orange", "D9732B"), ("Red", "C8484A"), ("Violet", "8A5CC8"), ("Pink", "C2578C"),
    };

    /// <summary>
    /// The colour the whole Material 3 palette is generated from: a row of round swatches, graphite first.
    /// </summary>
    public static StackPanel SeedPicker(string header, string current, Action<DockSettings, string> set)
    {
        var panel = new StackPanel
        {
            Spacing = 12, Tag = RowTag, Padding = new Thickness(16, 12, 16, 14), Background = M3.CardHigh(),
            CornerRadius = new CornerRadius(M3.CornerL),
        };
        panel.Children.Add(Label(header));
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var (name, hex) in Seeds)
        {
            ColorUtil.TryParseRgb(hex, out var rgb);
            bool on = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            double luma = (0.299 * rgb.R + 0.587 * rgb.G + 0.114 * rgb.B) / 255;
            var swatch = new Button
            {
                Width = 44, Height = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(on ? 3 : 0),
                BorderBrush = M3.Brush(M3.Current.OnSurface), Style = (Style)Application.Current.Resources["M3ButtonStyle"],
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, rgb.R, rgb.G, rgb.B)),
                Foreground = new SolidColorBrush(luma > 0.6 ? Colors.Black : Colors.White),
                Content = on ? new FontIcon { Glyph = "", FontSize = 16 } : null,
            };
            ToolTipService.SetToolTip(swatch, L.Text(name));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, L.Text(name));
            var chosen = hex;
            swatch.Click += (_, _) => S.Update(s => set(s, chosen));
            strip.Children.Add(swatch);
        }
        panel.Children.Add(strip);
        return panel;
    }

    /// <summary>Filled-tonal pill button (secondary container).</summary>
    public static Button Action(string text, Action click)
    {
        var b = new Button { Content = L.Text(text), HorizontalAlignment = HorizontalAlignment.Left };
        Tonal(b);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Restyles an existing button as a filled-tonal pill.</summary>
    public static Button Tonal(Button b)
    {
        var c = M3.Current;
        b.Style = (Style)Application.Current.Resources["M3ButtonStyle"];
        b.Background = M3.Brush(c.SecondaryContainer);
        b.Foreground = M3.Brush(c.OnSecondaryContainer);
        b.BorderThickness = new Thickness(0);
        b.CornerRadius = new CornerRadius(M3.CornerFull);
        b.Padding = new Thickness(24, 0, 24, 0);
        b.MinHeight = 40; b.Height = 40;
        b.FontWeight = Medium;
        return b;
    }

    /// <summary>A round 40 px icon button used inside list rows (reorder, remove…).</summary>
    public static Button IconButton(string glyph, string tip, Action click, bool enabled = true)
    {
        var c = M3.Current;
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Width = 40, Height = 40, Padding = new Thickness(0),
            Style = (Style)Application.Current.Resources["M3ButtonStyle"],
            Background = M3.Clear, Foreground = M3.Brush(c.OnSurfaceVariant), CornerRadius = new CornerRadius(M3.CornerFull), IsEnabled = enabled,
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, L.Text(tip));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, L.Text(tip));
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A list row with a title and trailing icon buttons (pinned apps, Launchpad tiles).</summary>
    public static Border ListRow(string title, params UIElement[] trailing)
    {
        var g = new Grid { ColumnSpacing = 4 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = Label(title);
        name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
        g.Children.Add(name);
        for (int i = 0; i < trailing.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)trailing[i], i + 1);
            g.Children.Add(trailing[i]);
        }
        var card = RowCard(g);
        card.MinHeight = 52; card.Padding = new Thickness(16, 6, 8, 6);
        return card;
    }

    public static Expander Advanced(params UIElement[] children)
    {
        var panel = Layout(children);
        panel.Margin = new Thickness(0, 4, 0, 4);
        return new Expander
        {
            Header = L.Text("Advanced"), Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = M3.CardHigh(), CornerRadius = new CornerRadius(M3.CornerL),
        };
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static string? HotkeyError(string text) =>
        text.Length == 0 || FloatingDock.Core.Services.HotkeyParser.TryParse(text, out _, out _) ? null : "Not a valid shortcut. Example: Ctrl+Alt+Space";
}
