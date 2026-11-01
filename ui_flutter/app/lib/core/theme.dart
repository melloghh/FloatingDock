import 'package:flutter/material.dart';

/// Material 3 colour roles from the user's seed colour (hex RGB, e.g. "4E9F75").
ColorScheme schemeFor(String seedHex, Brightness brightness) {
  final value = int.tryParse(seedHex.replaceFirst('#', ''), radix: 16);
  final seed = Color(0xFF000000 | ((value ?? 0x4E9F75) & 0xFFFFFF));
  return ColorScheme.fromSeed(seedColor: seed, brightness: brightness);
}

const fontFamily = 'Segoe UI Variable Text';
const fontFallback = ['Segoe UI', 'Arial'];

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
