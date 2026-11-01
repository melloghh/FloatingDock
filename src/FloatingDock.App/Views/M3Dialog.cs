using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Views;

/// <summary>Material 3 dialog: 28 px container, pill buttons (filled primary action, text dismissal).</summary>
internal static class M3Dialog
{
    internal static void Style(ContentDialog dialog)
    {
        var c = M3.Current;
        M3.Theme(dialog);
        dialog.Background = M3.Brush(c.ContainerHigh);
        dialog.Foreground = M3.Brush(c.OnSurface);
        dialog.CornerRadius = new CornerRadius(M3.CornerXl);
        dialog.PrimaryButtonStyle = Pill(filled: true, c);
        dialog.SecondaryButtonStyle = Pill(filled: false, c);
        dialog.CloseButtonStyle = Pill(filled: false, c);
    }

    private static Microsoft.UI.Xaml.Style Pill(bool filled, M3.Scheme c)
    {
        var s = new Microsoft.UI.Xaml.Style(typeof(Button)) { BasedOn = (Microsoft.UI.Xaml.Style)Application.Current.Resources["M3ButtonStyle"] };
        s.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(M3.CornerFull)));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(24, 0, 24, 0)));
        s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 40.0));
        s.Setters.Add(new Setter(Control.BackgroundProperty, filled ? M3.Brush(c.Primary) : M3.Clear));
        s.Setters.Add(new Setter(Control.ForegroundProperty, M3.Brush(filled ? c.OnPrimary : c.Primary)));
        s.Setters.Add(new Setter(Control.FontWeightProperty, new global::Windows.UI.Text.FontWeight { Weight = 500 }));
        return s;
    }
}
