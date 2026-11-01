import 'dart:ui' as ui;

import 'package:flutter/material.dart';

import '../core/theme.dart';

/// A surface that "pops" out of its anchor: scale 0.84 -> 1.04 -> 1, a short rise and a fade (about 400 ms).
/// It is drawn inside a transparent margin of the native window, so the overshoot is never clipped.
/// Hiding plays a quicker, calmer version in reverse and then calls [onHidden].
class PopSurface extends StatefulWidget {
  const PopSurface({
    super.key,
    required this.visible,
    required this.child,
    this.origin = Alignment.topCenter,
    this.onHidden,
    this.rise = 14,
  });

  final bool visible;
  final Widget child;
  final Alignment origin;
  final VoidCallback? onHidden;
  final double rise;

  @override
  State<PopSurface> createState() => _PopSurfaceState();
}

class _PopSurfaceState extends State<PopSurface> with SingleTickerProviderStateMixin {
  late final AnimationController _c = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 400),
    reverseDuration: const Duration(milliseconds: 150),
  );

  @override
  void initState() {
    super.initState();
    _c.addStatusListener((s) {
      if (s == AnimationStatus.dismissed && !widget.visible) widget.onHidden?.call();
    });
    if (widget.visible) _c.forward();
  }

  @override
  void didUpdateWidget(PopSurface old) {
    super.didUpdateWidget(old);
    if (widget.visible && !old.visible) {
      _c.forward(from: 0);
    } else if (!widget.visible && old.visible) {
      _c.reverse();
    }
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  static double _scaleIn(double t) {
    if (t < .65) return .84 + .20 * Curves.easeOutCubic.transform(t / .65);
    return 1.04 - .04 * Curves.easeInOut.transform((t - .65) / .35);
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _c,
      child: widget.child,
      builder: (context, child) {
        if (_c.isDismissed) return const SizedBox.shrink();
        final t = _c.value;
        final leaving = _c.status == AnimationStatus.reverse;
        final scale = leaving ? .94 + .06 * Curves.easeIn.transform(t) : _scaleIn(t);
        final opacity = Curves.easeOut.transform((t / .55).clamp(0.0, 1.0));
        final dy = (1 - Curves.easeOutCubic.transform(t)) * widget.rise;
        return Opacity(
          opacity: opacity,
          child: Transform.translate(
            offset: Offset(0, dy),
            child: Transform.scale(scale: scale, alignment: widget.origin, child: child),
          ),
        );
      },
    );
  }
}

/// "Animated content": the child rises, fades in and sharpens from a blurred copy of itself (about 650 ms).
/// Replays whenever [replayKey] changes. [index] staggers siblings.
class Reveal extends StatefulWidget {
  const Reveal({
    super.key,
    required this.child,
    this.replayKey,
    this.index = 0,
    this.milliseconds = 650,
    this.distance = 22,
    this.blur = 10,
  });

  final Widget child;
  final Object? replayKey;
  final int index;
  final int milliseconds;
  final double distance;
  final double blur;

  @override
  State<Reveal> createState() => _RevealState();
}

class _RevealState extends State<Reveal> with SingleTickerProviderStateMixin {
  late final AnimationController _c = AnimationController(vsync: this);

  Duration get _total => Duration(milliseconds: widget.milliseconds + widget.index.clamp(0, 12) * 45);

  @override
  void initState() {
    super.initState();
    _c.duration = _total;
    _c.forward();
  }

  @override
  void didUpdateWidget(Reveal old) {
    super.didUpdateWidget(old);
    if (old.replayKey != widget.replayKey) {
      _c.duration = _total;
      _c.forward(from: 0);
    }
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final lead = widget.index.clamp(0, 12) * 45 / _total.inMilliseconds;
    return AnimatedBuilder(
      animation: _c,
      child: widget.child,
      // The wrapper chain never changes shape (not even when the animation ends): swapping it for the bare child would
      // rebuild the whole subtree and lose its state. A finished reveal costs nothing: no layer is created.
      builder: (context, child) {
        final t = _c.isCompleted ? 1.0 : ((_c.value - lead) / (1 - lead)).clamp(0.0, 1.0);
        final e = reveal.transform(t);
        final sigma = widget.blur * (1 - e);
        return Opacity(
          opacity: Curves.easeOut.transform((t / .7).clamp(0.0, 1.0)),
          child: ImageFiltered(
            enabled: sigma > .3,
            imageFilter: ui.ImageFilter.blur(sigmaX: sigma + .001, sigmaY: sigma + .001, tileMode: TileMode.decal),
            child: Transform.translate(offset: Offset(0, (1 - e) * widget.distance), child: child),
          ),
        );
      },
    );
  }
}

/// One highlight that glides between the rows of a list instead of each row lighting up on its own.
/// Rows must have the same height ([itemExtent]); [gap] is the space between them.
class GlideList extends StatelessWidget {
  const GlideList({
    super.key,
    required this.count,
    required this.selected,
    required this.itemExtent,
    required this.itemBuilder,
    required this.highlight,
    this.gap = 0,
    this.radius = 16,
    this.milliseconds = 320,
  });

  final int count;
  final int? selected;
  final double itemExtent;
  final double gap;
  final double radius;
  final Color highlight;
  final int milliseconds;
  final Widget Function(BuildContext context, int index, bool selected) itemBuilder;

  @override
  Widget build(BuildContext context) {
    final step = itemExtent + gap;
    return SizedBox(
      height: count == 0 ? 0 : count * step - gap,
      child: Stack(
        children: [
          AnimatedPositioned(
            duration: Duration(milliseconds: milliseconds),
            curve: emphasized,
            left: 0,
            right: 0,
            top: (selected ?? 0) * step,
            height: itemExtent,
            child: AnimatedOpacity(
              duration: const Duration(milliseconds: 160),
              opacity: selected == null ? 0 : 1,
              child: DecoratedBox(
                decoration: BoxDecoration(color: highlight, borderRadius: BorderRadius.circular(radius)),
              ),
            ),
          ),
          for (var i = 0; i < count; i++)
            Positioned(left: 0, right: 0, top: i * step, height: itemExtent, child: itemBuilder(context, i, i == selected)),
        ],
      ),
    );
  }
}
