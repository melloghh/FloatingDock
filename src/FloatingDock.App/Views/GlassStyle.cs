using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Views;

/// <summary>Shared, wallpaper-tinted material. The backdrop supplies blur; these brushes supply legibility.</summary>
internal static class GlassStyle
{
    internal static SolidColorBrush Surface => Brush(176, 20, 35, 49);
    internal static SolidColorBrush Inset => Brush(18, 225, 238, 250);
    internal static SolidColorBrush Selected => Brush(100, 72, 105, 132);
    internal static SolidColorBrush Stroke => Brush(42, 222, 239, 255);
    internal static SolidColorBrush Secondary => Brush(255, 220, 230, 240);
    internal static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(ColorHelper.FromArgb(a,r,g,b));
    internal static Border Plate(UIElement child, double radius=22, double padding=12) => new()
    {
        Child=child, CornerRadius=new(radius), Padding=new(padding),
        Background=Surface, BorderBrush=Stroke, BorderThickness=new(1),
    };
    internal static void Button(Button button)
    {
        // Reuse the small compositor-friendly template. The stock Button tree
        // creates extra theme transitions and visual layers for every result/menu row.
        button.Style=(Style)Application.Current.Resources["MenuBarButtonStyle"];
        button.Background=Inset;button.BorderBrush=Stroke;button.BorderThickness=new(0);
        button.Resources["ButtonBackgroundPointerOver"]=Selected;
        button.Resources["ButtonBackgroundPressed"]=Brush(60,215,235,255);
        button.Resources["ButtonBorderBrushPointerOver"]=Stroke;
    }
}
