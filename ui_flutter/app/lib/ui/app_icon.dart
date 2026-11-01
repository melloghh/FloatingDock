import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../core/app_state.dart';

/// The picture of an app, as the engine resolves it. Shows [placeholder] (a tinted rounded square by default) until it arrives.
class AppIcon extends StatelessWidget {
  const AppIcon({super.key, required this.app, this.size = 32, this.fallback = true});
  final Map<String, Object?> app;
  final double size;
  final bool fallback;

  @override
  Widget build(BuildContext context) {
    final state = AppScope.of(context);
    final dpr = MediaQuery.devicePixelRatioOf(context);
    final px = (size * dpr).ceil().clamp(16, 256);
    final cs = Theme.of(context).colorScheme;
    return SizedBox(
      width: size,
      height: size,
      child: FutureBuilder<Uint8List?>(
        future: state.iconBytes(app, px, fallback: fallback),
        builder: (context, snap) {
          final bytes = snap.data;
          if (bytes == null) {
            return DecoratedBox(
              decoration: BoxDecoration(color: cs.surfaceContainerHighest, borderRadius: BorderRadius.circular(size * .22)),
            );
          }
          return Image.memory(bytes, width: size, height: size, fit: BoxFit.contain, gaplessPlayback: true, filterQuality: FilterQuality.medium);
        },
      ),
    );
  }
}
