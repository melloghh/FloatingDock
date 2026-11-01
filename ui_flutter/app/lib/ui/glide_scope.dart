import 'package:flutter/material.dart';

import '../core/theme.dart';

/// "Glide select" for pointer hover: one highlight that slides between the rows of a menu instead of each row
/// lighting up on its own. Rows call [GlideScope.of(context).hover] from a [GlideRow].
class GlideScope extends StatefulWidget {
  const GlideScope({super.key, required this.child, this.radius = Shapes.l, this.color});
  final Widget child;
  final double radius;
  final Color? color;

  static GlideScopeState of(BuildContext context) => context.findAncestorStateOfType<GlideScopeState>()!;

  @override
  State<GlideScope> createState() => GlideScopeState();
}

class GlideScopeState extends State<GlideScope> {
  Rect? _rect;

  /// Moves the highlight to the row whose context is [row].
  void hover(BuildContext row) {
    final scope = context.findRenderObject() as RenderBox?;
    final box = row.findRenderObject() as RenderBox?;
    if (scope == null || box == null || !scope.attached || !box.attached) return;
    final topLeft = box.localToGlobal(Offset.zero, ancestor: scope);
    final rect = topLeft & box.size;
    if (rect != _rect) setState(() => _rect = rect);
  }

  void leave() {
    if (_rect != null) setState(() => _rect = null);
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final rect = _rect;
    return MouseRegion(
      onExit: (_) => leave(),
      child: Stack(children: [
        AnimatedPositioned(
          duration: const Duration(milliseconds: 260),
          curve: emphasized,
          left: rect?.left ?? 0,
          top: rect?.top ?? 0,
          width: rect?.width ?? 0,
          height: rect?.height ?? 0,
          child: AnimatedOpacity(
            duration: const Duration(milliseconds: 140),
            opacity: rect == null ? 0 : 1,
            child: DecoratedBox(
              decoration: BoxDecoration(color: widget.color ?? cs.onSurface.withValues(alpha: .09), borderRadius: BorderRadius.circular(widget.radius)),
            ),
          ),
        ),
        widget.child,
      ]),
    );
  }
}

/// A pressable row inside a [GlideScope]: reports hover to the scope, paints no state layer of its own.
class GlideRow extends StatelessWidget {
  const GlideRow({super.key, required this.child, required this.onTap, this.enabled = true, this.radius = Shapes.l});
  final Widget child;
  final VoidCallback? onTap;
  final bool enabled;
  final double radius;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) {
        if (enabled && onTap != null) GlideScope.of(context).hover(context);
      },
      cursor: enabled && onTap != null ? SystemMouseCursors.click : SystemMouseCursors.basic,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: enabled ? onTap : null,
        child: child,
      ),
    );
  }
}
