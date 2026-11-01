using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Views;

/// <summary>
/// Shared surface roles for the floating windows. The names predate the Material 3 scheme; they now resolve
/// to its tonal roles so the search palette, menus, Launchpad and previews all follow the user's seed and theme.
/// The backdrop supplies blur; these brushes supply legibility.
/// </summary>
internal static class GlassStyle
{
    /// <summary>Window / popup base surface (translucent, blur shows through).</summary>
    internal static SolidColorBrush Surface => M3.Window();
    /// <summary>A card or field resting on the surface.</summary>
    internal static SolidColorBrush Inset => M3.CardHigh();
    /// <summary>Selected / active row: secondary container.</summary>
    internal static SolidColorBrush Selected => M3.Brush(M3.Current.SecondaryContainer);
    /// <summary>Hairline edge between a floating surface and what is behind it.</summary>
    internal static SolidColorBrush Stroke => M3.Brush(M3.Current.OutlineVariant, 120);
    internal static SolidColorBrush Secondary => M3.Brush(M3.Current.OnSurfaceVariant);
    internal static SolidColorBrush Primary => M3.Brush(M3.Current.OnSurface);
    internal static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(ColorHelper.FromArgb(a,r,g,b));
    internal static SolidColorBrush Clear => new(Colors.Transparent);
    internal static Border Plate(UIElement child, double radius=M3.CornerXl, double padding=12) => new()
    {
        Child=child, CornerRadius=new(radius), Padding=new(padding),
        Background=Inset, BorderBrush=Stroke, BorderThickness=new(0),
    };
    /// <summary>A flat list row / tonal button. Hover and press are state layers painted in the text colour.</summary>
    internal static void Button(Button button, bool flat=false)
    {
        // Reuse the small compositor-friendly template. The stock Button tree
        // creates extra theme transitions and visual layers for every result/menu row.
        button.Style=(Style)Application.Current.Resources["M3ButtonStyle"];
        var c=M3.Current;
        button.Background=flat?Clear:M3.Brush(c.ContainerHighest);
        button.Foreground=M3.Brush(c.OnSurface);
        button.BorderBrush=Clear;button.BorderThickness=new(0);
    }
}
