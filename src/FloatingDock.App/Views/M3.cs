using Color = global::Windows.UI.Color;
using FloatingDock.Core.Configuration;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Views;

/// <summary>
/// Material 3 design tokens: tonal colour roles generated from one seed colour (graphite by default),
/// the shape scale, state-layer opacities and motion. Surfaces stay translucent so the backdrop blur shows through.
/// </summary>
internal static class M3
{
    internal const string DefaultSeed = "4E9F75";

    // Shape scale (dp).
    internal const double CornerXs = 4, CornerS = 8, CornerM = 12, CornerL = 16, CornerXl = 28, CornerFull = 20;
    // State layers: opacity of on-surface painted over a surface.
    internal const byte HoverAlpha = 20, PressedAlpha = 26, FocusAlpha = 26;
    // Standard "emphasized decelerate" curve, shared with the web reference (0.2, 0, 0, 1).
    internal static readonly (double X1, double Y1, double X2, double Y2) Emphasized = (0.2, 0, 0, 1);

    internal sealed record Scheme(
        bool Dark,
        Color Surface, Color ContainerLowest, Color ContainerLow, Color Container, Color ContainerHigh, Color ContainerHighest,
        Color OnSurface, Color OnSurfaceVariant, Color Outline, Color OutlineVariant,
        Color Primary, Color OnPrimary, Color PrimaryContainer, Color OnPrimaryContainer,
        Color SecondaryContainer, Color OnSecondaryContainer, Color Error, Color OnError);

    private static Scheme? _scheme;
    private static string _key = "";
    /// <summary>The scheme for the user's current seed and theme. Every window reads this, so a settings change reaches all of them.</summary>
    internal static Scheme Current
    {
        get
        {
            try { return Apply(DockApp.Current.Settings.Current); }
            catch { return _scheme ?? Build(DefaultSeed, true); }
        }
    }
    internal static bool Dark => Current.Dark;
    private static Scheme? _bar;private static string _barSeed = "";
    /// <summary>The top bar is always a dark shell, so it uses the dark scheme of the current seed whatever the theme is.</summary>
    internal static Scheme Bar { get { _ = Current; return _bar ?? Build(DefaultSeed, true); } }
    internal static ElementTheme ElementTheme => Dark ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light;

    /// <summary>Rebuilds the scheme when the seed or theme changed. Cheap to call on every show.</summary>
    internal static Scheme Apply(DockSettings s)
    {
        bool dark = s.Theme switch { ThemeMode.Light => false, ThemeMode.System => SystemIsDark(), _ => true };
        string seed = ColorUtil.TryParseRgb(s.AccentSeed, out _) ? s.AccentSeed.TrimStart('#').ToUpperInvariant() : DefaultSeed;
        string key = seed + (dark ? "d" : "l");
        if (_scheme is null || key != _key) { _scheme = Build(seed, dark); _key = key; }
        if (_bar is null || _barSeed != seed) { _bar = dark ? _scheme : Build(seed, true); _barSeed = seed; }
        return _scheme;
    }

    private static bool SystemIsDark()
    {
        try { var c = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Background); return c.R + c.G + c.B < 384; }
        catch { return true; }
    }

    internal static SolidColorBrush Brush(Color c) => new(c);
    internal static SolidColorBrush Brush(Color c, byte alpha) => new(ColorHelper.FromArgb(alpha, c.R, c.G, c.B));
    internal static SolidColorBrush Clear => new(Colors.Transparent);
    /// <summary>On-surface painted at a state-layer opacity, for hover / pressed overlays.</summary>
    internal static SolidColorBrush State(byte alpha) => Brush(Current.OnSurface, alpha);

    /// <summary>Surfaces sit over the blurred backdrop, so they are translucent in proportion to how "far back" they are.</summary>
    internal static SolidColorBrush Window() => Brush(Current.ContainerLow, Current.Dark ? (byte)214 : (byte)226);
    internal static SolidColorBrush Card() => Brush(Current.Container, Current.Dark ? (byte)240 : (byte)244);
    internal static SolidColorBrush CardHigh() => Brush(Current.ContainerHigh, 250);
    internal static SolidColorBrush Field() => Brush(Current.ContainerHighest, 255);
    internal static SolidColorBrush Text(bool secondary = false) => Brush(secondary ? Current.OnSurfaceVariant : Current.OnSurface);

    /// <summary>
    /// Fills a window root's resource dictionary so stock WinUI controls (Slider, ComboBox, TextBox,
    /// ContentDialog, Flyout, ScrollBar…) adopt the scheme without per-control styling.
    /// </summary>
    internal static void Theme(FrameworkElement root)
    {
        var c = Current;
        var r = root.Resources;
        root.RequestedTheme = ElementTheme;
        void Set(string key, Color color, byte? alpha = null) => r[key] = alpha is { } a ? Brush(color, a) : Brush(color);
        Set("M3SwitchTrackOff", c.ContainerHighest);
        Set("M3SwitchTrackOn", c.Primary);
        Set("M3SwitchOutline", c.Outline);
        Set("M3SwitchThumbOff", c.Outline);
        Set("M3SwitchThumbOn", c.OnPrimary);
        Set("TextFillColorPrimaryBrush", c.OnSurface);
        Set("TextFillColorSecondaryBrush", c.OnSurfaceVariant);
        Set("TextFillColorTertiaryBrush", c.OnSurfaceVariant, 190);
        Set("TextFillColorDisabledBrush", c.OnSurface, 96);
        Set("AccentFillColorDefaultBrush", c.Primary);
        Set("AccentFillColorSecondaryBrush", c.Primary, 230);
        Set("AccentFillColorTertiaryBrush", c.Primary, 200);
        Set("TextOnAccentFillColorPrimaryBrush", c.OnPrimary);
        Set("AccentTextFillColorPrimaryBrush", c.Primary);
        // Accent button styles resolve these at app scope, so the system accent colour would win unless they are set directly.
        Set("AccentButtonBackground", c.Primary);
        Set("AccentButtonBackgroundPointerOver", c.Primary, 235);
        Set("AccentButtonBackgroundPressed", c.Primary, 215);
        Set("AccentButtonBackgroundDisabled", c.OnSurface, 30);
        Set("AccentButtonForeground", c.OnPrimary);
        Set("AccentButtonForegroundPointerOver", c.OnPrimary);
        Set("AccentButtonForegroundPressed", c.OnPrimary);
        Set("AccentButtonBorderBrush", c.Primary, 0);
        Set("AccentButtonBorderBrushPointerOver", c.Primary, 0);
        Set("AccentButtonBorderBrushPressed", c.Primary, 0);
        Set("ToggleButtonBackgroundChecked", c.Primary);
        Set("ToggleButtonForegroundChecked", c.OnPrimary);
        // Slider: thick pill track in primary, round thumb.
        Set("SliderTrackFill", c.ContainerHighest);
        Set("SliderTrackFillPointerOver", c.ContainerHighest);
        Set("SliderTrackValueFill", c.Primary);
        Set("SliderTrackValueFillPointerOver", c.Primary);
        Set("SliderTrackValueFillPressed", c.Primary);
        Set("SliderThumbBackground", c.Primary);
        Set("SliderThumbBackgroundPointerOver", c.Primary);
        Set("SliderThumbBackgroundPressed", c.Primary);
        r["SliderTrackThemeHeight"] = 10.0;
        // Text fields: filled, no underline chrome.
        Set("TextControlBackground", c.ContainerHighest);
        Set("TextControlBackgroundPointerOver", c.ContainerHighest);
        Set("TextControlBackgroundFocused", c.ContainerHighest);
        Set("TextControlBorderBrush", c.OutlineVariant, 0);
        Set("TextControlBorderBrushPointerOver", c.OutlineVariant, 0);
        Set("TextControlBorderBrushFocused", c.Primary);
        Set("TextControlForeground", c.OnSurface);
        Set("TextControlForegroundPointerOver", c.OnSurface);
        Set("TextControlForegroundFocused", c.OnSurface);
        Set("TextControlPlaceholderForeground", c.OnSurfaceVariant, 190);
        Set("TextControlHeaderForeground", c.OnSurfaceVariant);
        r["TextControlBorderThemeThickness"] = new Thickness(0);
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(0, 0, 0, 2);
        // Combo box: filled field, container-high menu.
        Set("ComboBoxBackground", c.ContainerHighest);
        Set("ComboBoxBackgroundPointerOver", c.ContainerHighest);
        Set("ComboBoxBackgroundPressed", c.ContainerHighest);
        Set("ComboBoxBackgroundFocused", c.ContainerHighest);
        Set("ComboBoxBorderBrush", c.OutlineVariant, 0);
        Set("ComboBoxBorderBrushPointerOver", c.OutlineVariant, 0);
        Set("ComboBoxBorderBrushPressed", c.OutlineVariant, 0);
        Set("ComboBoxBorderBrushFocused", c.Primary);
        Set("ComboBoxForeground", c.OnSurface);
        Set("ComboBoxForegroundPointerOver", c.OnSurface);
        Set("ComboBoxForegroundPressed", c.OnSurface);
        Set("ComboBoxDropDownBackground", c.ContainerHigh);
        Set("ComboBoxDropDownBorderBrush", c.OutlineVariant, 0);
        Set("ComboBoxDropDownForeground", c.OnSurface);
        Set("ComboBoxItemBackgroundPointerOver", c.OnSurface, 20);
        Set("ComboBoxItemBackgroundPressed", c.OnSurface, 30);
        Set("ComboBoxItemBackgroundSelected", c.SecondaryContainer);
        Set("ComboBoxItemBackgroundSelectedPointerOver", c.SecondaryContainer);
        Set("ComboBoxItemBackgroundSelectedPressed", c.SecondaryContainer);
        Set("ComboBoxItemForeground", c.OnSurface);
        Set("ComboBoxItemForegroundSelected", c.OnSecondaryContainer);
        Set("ComboBoxItemForegroundPointerOver", c.OnSurface);
        Set("ComboBoxItemForegroundSelectedPointerOver", c.OnSecondaryContainer);
        Set("ComboBoxItemPillFillBrush", c.Primary);
        Set("ComboBoxItemPillFillBrushPressed", c.Primary);
        // Dialog / flyout / menu surfaces.
        Set("ContentDialogBackground", c.ContainerHigh);
        Set("ContentDialogBorderBrush", c.OutlineVariant, 0);
        Set("ContentDialogForeground", c.OnSurface);
        Set("ContentDialogTopOverlay", c.ContainerHigh);
        Set("ContentDialogSmokeFill", c.Dark ? Colors.Black : Colors.Black, c.Dark ? (byte)130 : (byte)90);
        Set("FlyoutPresenterBackground", c.ContainerHigh);
        Set("FlyoutBorderThemeBrush", c.OutlineVariant, 0);
        Set("MenuFlyoutPresenterBackground", c.ContainerHigh);
        Set("MenuFlyoutPresenterBorderBrush", c.OutlineVariant, 0);
        Set("MenuFlyoutItemBackgroundPointerOver", c.OnSurface, 20);
        Set("MenuFlyoutItemForeground", c.OnSurface);
        Set("ToolTipBackground", c.ContainerHighest);
        Set("ToolTipForeground", c.OnSurface);
        Set("ToolTipBorderBrush", c.OutlineVariant, 0);
        r["OverlayCornerRadius"] = new CornerRadius(CornerXl);
        r["ControlCornerRadius"] = new CornerRadius(CornerM);
        // Scroll bars: slim pill thumb in the neutral on-surface colour.
        Set("ScrollBarThumbFill", c.OnSurfaceVariant, 110);
        Set("ScrollBarThumbFillPointerOver", c.OnSurfaceVariant, 170);
        Set("ScrollBarThumbFillPressed", c.OnSurfaceVariant, 210);
        Set("ScrollBarTrackFill", c.Container, 0);
        Set("ScrollBarTrackFillPointerOver", c.Container, 0);
        // Expander / calendar / date picker backgrounds follow the card tone.
        Set("ExpanderHeaderBackground", c.ContainerHigh);
        Set("ExpanderContentBackground", c.ContainerHigh);
        Set("ExpanderHeaderBorderBrush", c.OutlineVariant, 0);
        Set("ExpanderContentBorderBrush", c.OutlineVariant, 0);
        Set("CalendarViewBackground", c.ContainerHigh);
        Set("CalendarViewCalendarItemForeground", c.OnSurface);
        Set("CalendarViewSelectedBorderBrush", c.Primary);
        Set("CalendarViewTodayForeground", c.OnPrimary);
        Set("CalendarViewTodayBackground", c.Primary);
        Set("CalendarViewOutOfScopeForeground", c.OnSurfaceVariant, 160);
    }

    // ---------------------------------------------------------------- tonal palette (OKLCH)

    private static Scheme Build(string seedHex, bool dark)
    {
        ColorUtil.TryParseRgb(seedHex, out var rgb);
        var (_, seedC, hue) = ToOklch(rgb.R, rgb.G, rgb.B);
        double primaryC = Math.Min(seedC, 0.16);
        double neutralC = Math.Min(seedC * 0.2, 0.024);        // surfaces: a visible hint of the seed
        double variantC = Math.Min(seedC * 0.3, 0.036);
        double secondaryC = Math.Min(seedC * 0.45, 0.07);
        Color N(double tone) => Tone(tone, neutralC, hue);
        Color V(double tone) => Tone(tone, variantC, hue);
        Color P(double tone) => Tone(tone, primaryC, hue);
        Color S(double tone) => Tone(tone, secondaryC, hue);
        // Error keeps its own hue regardless of the seed (Material error role).
        Color E(double tone) => Tone(tone, 0.14, 0.5);
        return dark
            ? new Scheme(true, N(6), N(4), N(10), N(12), N(17), N(22), N(90), V(80), V(60), V(30),
                P(80), P(20), P(30), P(90), S(30), S(90), E(80), E(20))
            : new Scheme(false, N(98), N(100), N(96), N(94), N(92), N(90), N(10), V(30), V(50), V(80),
                P(40), P(100), P(90), P(10), S(90), S(10), E(40), E(100));
    }

    // CIE-L*-style tone → OKLab lightness (exact for greys: L = cbrt(Y)).
    private static Color Tone(double tone, double chroma, double hue)
    {
        double y = tone > 8 ? Math.Pow((tone + 16) / 116, 3) : tone / 903.3;
        double l = Math.Cbrt(y);
        // Reduce chroma until the colour fits in sRGB.
        for (int i = 0; i < 24; i++, chroma *= 0.85)
        {
            var (r, g, b) = FromOklch(l, chroma, hue);
            if (r is >= -0.001 and <= 1.001 && g is >= -0.001 and <= 1.001 && b is >= -0.001 and <= 1.001) return Out(r, g, b);
        }
        var (gr, gg, gb) = FromOklch(l, 0, hue);
        return Out(gr, gg, gb);
    }

    private static Color Out(double r, double g, double b) =>
        ColorHelper.FromArgb(255, Gamma(r), Gamma(g), Gamma(b));

    private static byte Gamma(double v)
    {
        v = Math.Clamp(v, 0, 1);
        v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    private static double Linear(byte c)
    {
        double v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static (double L, double C, double H) ToOklch(byte r8, byte g8, byte b8)
    {
        double r = Linear(r8), g = Linear(g8), b = Linear(b8);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        double L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        double a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        double bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        return (L, Math.Sqrt(a * a + bb * bb), Math.Atan2(bb, a));
    }

    private static (double R, double G, double B) FromOklch(double L, double C, double H)
    {
        double a = C * Math.Cos(H), b = C * Math.Sin(H);
        double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
               -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
               -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }
}
