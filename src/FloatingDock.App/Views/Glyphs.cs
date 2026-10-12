using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FloatingDock.UI.Views;

public enum GlyphKind { App, Start, Launchpad }

/// <summary>Drawn fallbacks used while an icon loads or when no icon exists. They are scaled with a Viewbox, so they stay crisp at any size.</summary>
internal static class Glyphs
{
    public static UIElement Create(GlyphKind kind, string? name)
    {
        FrameworkElement inner = kind switch
        {
            GlyphKind.Start => StartGlyph(),
            GlyphKind.Launchpad => LaunchpadGlyph(),
            _ => AppGlyph(name),
        };
        return new Viewbox { Stretch = Stretch.Uniform, Child = inner, IsHitTestVisible = false };
    }

    private static Grid Tile(Brush background)
    {
        var g = new Grid { Width = 48, Height = 48 };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = background });
        return g;
    }

    private static FrameworkElement StartGlyph()
    {
        var g = Tile(new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0), EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops = { new GradientStop { Color = Color.FromArgb(255, 43, 126, 230), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 20, 60, 150), Offset = 1 } },
        });
        var grid = new Grid { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RowSpacing = 2, ColumnSpacing = 2 };
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle { Fill = new SolidColorBrush(Colors.White), RadiusX = 1.5, RadiusY = 1.5 };
            Grid.SetRow(r, i / 2); Grid.SetColumn(r, i % 2);
            grid.Children.Add(r);
        }
        g.Children.Add(grid);
        return g;
    }

    private static FrameworkElement LaunchpadGlyph()
    {
        var g = Tile(new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0), EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops = { new GradientStop { Color = Color.FromArgb(255, 36, 39, 45), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 8, 10, 14), Offset = 1 } },
        });
        var grid = new Grid { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++) { grid.RowDefinitions.Add(new RowDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition()); }
        Color[] pal = { Color.FromArgb(255, 255, 95, 86), Color.FromArgb(255, 255, 189, 46), Color.FromArgb(255, 39, 201, 63),
                        Color.FromArgb(255, 10, 132, 255), Color.FromArgb(255, 175, 82, 222), Color.FromArgb(255, 255, 255, 255),
                        Color.FromArgb(255, 255, 140, 0), Color.FromArgb(255, 50, 173, 230), Color.FromArgb(255, 60, 60, 70) };
        for (int i = 0; i < 9; i++)
        {
            var d = new Ellipse { Width = 7.5, Height = 7.5, Fill = new SolidColorBrush(pal[i]), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(d, i / 3); Grid.SetColumn(d, i % 3);
            grid.Children.Add(d);
        }
        g.Children.Add(grid);
        return g;
    }

    private static FrameworkElement AppGlyph(string? name)
    {
        var text = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
        int hash = 17; foreach (var ch in name ?? "") hash = hash * 31 + ch;
        double hue = Math.Abs(hash) % 360;
        var g = Tile(new SolidColorBrush(FromHsv(hue, 0.55, 0.62)));
        g.Children.Add(new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(Colors.White), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
