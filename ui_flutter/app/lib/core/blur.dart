import 'package:flutter/rendering.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter/widgets.dart';

import 'app_state.dart';
import 'native.dart';

/// Blurs the desktop behind the translucent surfaces of one window (setting `Blur`).
///
/// Windows blurs a region of the window, so the shapes of the surfaces are measured after every frame in which they may have
/// moved and sent to the native side only when they changed. Nothing is measured or sent while the setting is off.
class BlurScope extends StatefulWidget {
  const BlurScope({super.key, required this.child});
  final Widget child;

  static BlurScopeState? maybeOf(BuildContext context) => context.findAncestorStateOfType<BlurScopeState>();

  @override
  State<BlurScope> createState() => BlurScopeState();
}

class BlurScopeState extends State<BlurScope> {
  final _boxes = <RenderBlurSurface>{};
  final _extras = <Object, (Rect, double)>{};
  bool _enabled = false, _queued = false, _sentAny = false;
  String _sent = '';

  void attach(RenderBlurSurface box) {
    _boxes.add(box);
    markDirty();
  }

  void detach(RenderBlurSurface box) {
    _boxes.remove(box);
    markDirty();
  }

  /// A surface that is not a widget (the dock plate is painted) reports its rectangle in view coordinates.
  void setExtra(Object key, Rect? rect, double radius) {
    if (rect == null) {
      _extras.remove(key);
    } else {
      _extras[key] = (rect, radius);
    }
    markDirty();
  }

  void markDirty() {
    if (_queued || !_enabled && !_sentAny) return;
    _queued = true;
    SchedulerBinding.instance.addPostFrameCallback((_) {
      _queued = false;
      if (mounted) _flush();
    });
  }

  void _flush() {
    final shapes = <(Rect, double)>[];
    if (_enabled) {
      for (final box in _boxes) {
        if (!box.attached || !box.hasSize) continue;
        final topLeft = box.localToGlobal(Offset.zero);
        shapes.add((topLeft & box.size, box.radius));
      }
      shapes.addAll(_extras.values);
    }
    final key = [for (final (r, rad) in shapes) '${r.left.round()},${r.top.round()},${r.width.round()},${r.height.round()},${rad.round()}'].join(';');
    if (key == _sent) return;
    _sent = key;
    _sentAny = shapes.isNotEmpty;
    final viewId = View.of(context).viewId;
    native.setBlur(viewId, [for (final (r, rad) in shapes) (r, rad)]).catchError((_) {});
  }

  @override
  Widget build(BuildContext context) {
    final on = AppScope.of(context).setting<bool>('Blur', false);
    if (on != _enabled) {
      _enabled = on;
      markDirty();
    }
    return widget.child;
  }
}

/// Marks its child as a surface whose backdrop is blurred when the setting is on.
class BlurSurface extends SingleChildRenderObjectWidget {
  const BlurSurface({super.key, required this.radius, required super.child});
  final double radius;

  @override
  RenderObject createRenderObject(BuildContext context) => RenderBlurSurface(radius, BlurScope.maybeOf(context));

  @override
  void updateRenderObject(BuildContext context, RenderBlurSurface renderObject) {
    renderObject
      ..radius = radius
      ..scope = BlurScope.maybeOf(context);
  }
}

class RenderBlurSurface extends RenderProxyBox {
  RenderBlurSurface(this.radius, this._scope);
  double radius;
  BlurScopeState? _scope;

  set scope(BlurScopeState? value) {
    if (identical(value, _scope)) return;
    if (attached) _scope?.detach(this);
    _scope = value;
    if (attached) _scope?.attach(this);
  }

  @override
  void attach(PipelineOwner owner) {
    super.attach(owner);
    _scope?.attach(this);
  }

  @override
  void detach() {
    _scope?.detach(this);
    super.detach();
  }

  @override
  void performLayout() {
    super.performLayout();
    _scope?.markDirty();
  }

  @override
  void paint(PaintingContext context, Offset offset) {
    _scope?.markDirty();
    super.paint(context, offset);
  }
}
