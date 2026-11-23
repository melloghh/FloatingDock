import 'package:flutter/material.dart';

/// The colour the palette is generated from unless the user picks another: a soft blue that gives the lavender accent
/// and the blue-black surfaces of the reference.
const defaultSeed = '4A6BC8';

/// Material 3 colour roles from the user's seed colour (hex RGB, e.g. "4A6BC8").
/// A seed without any hue (R = G = B) gives a truly neutral palette (no tint in the surfaces or the accent).
///
/// The dark scheme is pushed one step darker than stock Material 3: the window is near-black, side panels sit on the plain
/// surface, rows and buttons one step above, as in the reference.
ColorScheme schemeFor(String seedHex, Brightness brightness) {
  final value = int.tryParse(seedHex.replaceFirst('#', ''), radix: 16);
  final rgb = (value ?? int.parse(defaultSeed, radix: 16)) & 0xFFFFFF;
  final r = rgb >> 16, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
  final base = ColorScheme.fromSeed(
    seedColor: Color(0xFF000000 | rgb),
    brightness: brightness,
    dynamicSchemeVariant: r == g && g == b ? DynamicSchemeVariant.monochrome : DynamicSchemeVariant.tonalSpot,
  );
  if (brightness != Brightness.dark) return base;
  Color toward(Color c, Color to, double t) => Color.lerp(c, to, t)!;
  return base.copyWith(
    surface: toward(base.surface, Colors.black, .35),
    surfaceContainerLowest: toward(base.surface, Colors.black, .55),
    surfaceContainerLow: base.surface,
    surfaceContainer: toward(base.surface, base.surfaceContainerLow, .4),
    surfaceContainerHigh: base.surfaceContainerLow,
    surfaceContainerHighest: base.surfaceContainer,
  );
}

/// The reference is set entirely in a monospace face (Cascadia Mono ships with Windows 11; Consolas is the fallback).
const fontFamily = 'Cascadia Mono';
const fontFallback = ['Consolas', 'Segoe UI Variable Text', 'Segoe UI', 'Arial'];

/// Shape scale from the reference: 4 / 8 / 12 / 16 / 28 dp.
abstract final class Shapes {
  static const xs = 4.0, s = 8.0, m = 12.0, l = 16.0, xl = 28.0;
}

/// The emphasized-decelerate curve of the reference (0.2, 0, 0, 1).
const Curve emphasized = Cubic(0.2, 0, 0, 1);

/// The curve of the "animated content" reveal.
const Curve reveal = Cubic(0.22, 0.7, 0.2, 1);

ThemeData buildTheme(ColorScheme cs) {
  final base = ThemeData(
    useMaterial3: true,
    colorScheme: cs,
    fontFamily: fontFamily,
    fontFamilyFallback: fontFallback,
    splashFactory: InkSparkle.splashFactory,
    visualDensity: VisualDensity.standard,
    scrollbarTheme: ScrollbarThemeData(
      thumbColor: WidgetStatePropertyAll(cs.onSurfaceVariant.withValues(alpha: .45)),
      radius: const Radius.circular(8),
      thickness: const WidgetStatePropertyAll(6),
    ),
  );
  return base.copyWith(
    // A light round thumb on a dark track when off; a dark thumb on the lavender track when on.
    switchTheme: SwitchThemeData(
      thumbColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? cs.onPrimary : cs.onSurface.withValues(alpha: .9)),
      trackColor: WidgetStateProperty.resolveWith((s) => s.contains(WidgetState.selected) ? cs.primary : cs.surfaceContainerHighest),
      trackOutlineColor: const WidgetStatePropertyAll(Colors.transparent),
      thumbIcon: const WidgetStatePropertyAll(null),
      trackOutlineWidth: const WidgetStatePropertyAll(0),
    ),
    dialogTheme: DialogThemeData(
      backgroundColor: cs.surfaceContainerHigh,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(Shapes.xl)),
    ),
    popupMenuTheme: PopupMenuThemeData(
      color: cs.surfaceContainerHigh,
      surfaceTintColor: Colors.transparent,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(Shapes.l)),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: cs.surfaceContainerHighest,
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(Shapes.m), borderSide: BorderSide.none),
      enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(Shapes.m), borderSide: BorderSide.none),
      focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(Shapes.m), borderSide: BorderSide(color: cs.primary, width: 2)),
      contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
    ),
    tooltipTheme: TooltipThemeData(
      decoration: BoxDecoration(color: cs.surfaceContainerHighest, borderRadius: BorderRadius.circular(Shapes.s)),
      textStyle: TextStyle(color: cs.onSurface, fontSize: 12),
    ),
  );
}
