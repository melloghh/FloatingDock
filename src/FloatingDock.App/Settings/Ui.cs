using FloatingDock.UI.Views;
using FloatingDock.Core.Configuration;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Settings;

/// <summary>Small code-only control builders. Every control writes straight into <see cref="SettingsStore"/>.</summary>
internal static class Ui
{
    private static SettingsStoreAccess S => new();
    internal readonly struct SettingsStoreAccess { public void Update(Action<DockSettings> a) => DockApp.Current.Settings.Update(a); }

    public static TextBlock Title(string text) => new() { Text = L.Text(text), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    public static TextBlock Note(string text) => new()
    {
        Text = L.Text(text), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12.5,
    };

    public static Border Section(string title, params UIElement[] children)
    {
        var p = new StackPanel { Spacing = 14 };
        p.Children.Add(new TextBlock { Text = L.Text(title), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.9 });
        foreach (var c in children) p.Children.Add(c);
        return new Border { Child=p,Padding=new Thickness(20),Margin=new Thickness(0,8,0,0),CornerRadius=new CornerRadius(14),Background=new SolidColorBrush(ColorHelper.FromArgb(14,180,194,218)),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(22,180,194,218)),BorderThickness=new Thickness(1) };
    }

    public static ToggleSwitch Toggle(string header, bool value, Action<DockSettings, bool> set) 
    {
        var t = new ToggleSwitch { Header = L.Text(header), IsOn = value, OnContent = L.Text("On"), OffContent = L.Text("Off") };
        t.Toggled += (_, _) => S.Update(s => set(s, t.IsOn));
        return t;
    }

    public static UIElement Slider(string header, int min, int max, int value, Action<DockSettings, int> set, string suffix = "")
    {
        var label = new TextBlock { Text = $"{L.Text(header)}: {value}{suffix}" };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, StepFrequency = 1, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            label.Text = $"{L.Text(header)}: {v}{suffix}";
            S.Update(s => set(s, v));
        };
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(label);
        p.Children.Add(slider);
        return p;
    }

    public static ComboBox Combo<T>(string header, IReadOnlyList<(string Label, T Value)> items, T selected, Action<DockSettings, T> set) where T : notnull
    {
        var c = new ComboBox { Header = L.Text(header), MinWidth = 260 };
        foreach (var (label, _) in items) c.Items.Add(L.Text(label));
        c.SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedIndex < 0) return;
            var v = items[c.SelectedIndex].Value;
            S.Update(s => set(s, v));
        };
        return c;
    }

    /// <summary>Text box that commits on Enter / focus loss. <paramref name="validate"/> returns an error text or null.</summary>
    public static StackPanel TextField(string header, string value, Func<string, string?> validate, Action<DockSettings, string> set, string? placeholder = null)
    {
        var box = new TextBox { Header = L.Text(header), Text = value, Width = 260, PlaceholderText = L.Text(placeholder ?? ""), HorizontalAlignment = HorizontalAlignment.Left };
        var error = new TextBlock { Foreground = new SolidColorBrush(Colors.OrangeRed), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
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
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(box);
        p.Children.Add(error);
        return p;
    }

    public static Button Action(string text, Action click)
    {
        var b = new Button { Content = L.Text(text),CornerRadius=new CornerRadius(8),Padding=new Thickness(12,8,12,8) };
        b.Click += (_, _) => click();
        return b;
    }

    public static Expander Advanced(params UIElement[] children)
    {
        var panel=new StackPanel { Spacing=14 };
        foreach(var child in children)panel.Children.Add(child);
        return new Expander { Header=L.Text("Advanced"),Content=panel,HorizontalAlignment=HorizontalAlignment.Stretch };
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
