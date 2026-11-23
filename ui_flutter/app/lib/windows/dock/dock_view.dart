import 'dart:async';
import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter/services.dart';

import '../../core/app_state.dart';
import '../../core/blur.dart';
import '../../core/theme.dart';
import 'dock_math.dart';

/// Maps the dock's own horizontal coordinates (a along the dock, c from the top of the window towards the edge it sits on)
/// to window coordinates, for a dock on the bottom, left or right edge.
class DockAxis {
  const DockAxis(this.edge, this.cross);
  final String edge; // Bottom | Left | Right
  final double cross; // window extent across the dock (logical height of a bottom dock)

  bool get vertical => edge != 'Bottom';

  Offset point(double a, double c) => switch (edge) {
        'Left' => Offset(cross - c, a),
        'Right' => Offset(c, a),
        _ => Offset(a, c),
      };

  Rect rect(double a0, double c0, double a1, double c1) {
    final p = point(a0, c0), q = point(a1, c1);
    return Rect.fromPoints(p, q);
  }

  /// Window size for a dock whose along-extent is [along].
  Size size(double along) => vertical ? Size(cross, along) : Size(along, cross);

  (double a, double c) logical(Offset p) => switch (edge) {
        'Left' => (p.dy, cross - p.dx),
        'Right' => (p.dy, p.dx),
        _ => (p.dx, p.dy),
      };

  /// A movement towards the screen edge by [d] in the dock's own c direction.
  Offset toEdge(double d) => switch (edge) {
        'Left' => Offset(-d, 0),
        'Right' => Offset(d, 0),
        _ => Offset(0, d),
      };
}

/// The state, layout and input of one dock window. The widget only paints and feeds pointer events into it.
class DockModel extends ChangeNotifier {
  DockModel({required this.state, Map<String, Object?>? settings}) : _settings = settings ?? state.settings {
    gp = GeometryParams.fromSettings(_settings);
    metrics = WindowMetrics.compute(gp, 1, 0);
    zones = metrics;
  }

  final AppState state;
  Map<String, Object?> _settings;

  late GeometryParams gp;
  late WindowMetrics metrics;

  /// The hover zones for the icons that are really there (the window itself is sized for a few more).
  late WindowMetrics zones;
  int _capacity = 0;
  final motion = DockMotion();
  final mag = MagnificationState();
  final layout = DockLayoutResult();
  List<DockEntry> entries = const [];
  bool _firstEntries = true;

  /// Along-extent of the window the controller gave us (logical); the metrics grow it when the item set outgrows it.
  double alongSlack = 2;
  (int, int) _shape = (0, 0);

  // pointer state
  int hoverIndex = -1;
  int pressIndex = -1;
  Offset pressPos = Offset.zero;
  bool dragging = false;
  String? dragKey;
  bool menuOpen = false, suspended = false, userHidden = false, concealed = false, kbMode = false;

  /// A window preview is open above the icon: the name label would sit behind it, so it steps aside.
  bool previewActive = false;
  /// Set by the controller when the cursor reached the screen edge while a maximized window conceals the dock: the dock
  /// then behaves like an auto-hidden one until the cursor leaves it again.
  bool peek = false;
  int kbIndex = -1;
  double labelAlpha = 0;
  String? labelKey;
  double hide = 0; // 0 shown .. 1 slid away
  double idleFor = 0;
  final pulses = <String, double>{}; // key -> seconds since the click
  bool pointerInside = false, previewInside = false;
  Timer? _dwell;

  // callbacks set by the controller
  void Function(DockEntry entry, {bool newInstance})? onActivate;
  void Function(DockEntry? entry, Offset windowPoint)? onContextMenu;
  void Function(String key, int index)? onMovePinned;
  void Function(String key)? onUnpin;
  void Function(DockEntry entry, Rect windowRect)? onPreview;
  void Function()? onPreviewHide;
  void Function(Size windowSize)? onSizeChanged;
  void Function()? onNeedFrame;
  void Function()? onLeftKbMode;

  DockAxis get axis => DockAxis('${_settings['Edge'] ?? 'Bottom'}', metrics.windowHeight);
  Size get windowSize => axis.size(metrics.windowWidth);

  bool _b(String k, bool f) => _settings[k] is bool ? _settings[k] as bool : f;
  int _i(String k, int f) => (_settings[k] as num?)?.toInt() ?? f;

  // ------------------------------------------------------------------ inputs

  void updateSettings(Map<String, Object?> settings) {
    _settings = settings;
    gp = GeometryParams.fromSettings(settings);
    _shape = (-1, -1);
    _recomputeMetrics(force: true);
    requestFrame();
  }

  void setEntries(List<DockEntry> next) {
    entries = next;
    motion.setEntries(next, animate: !_firstEntries && _b('IconAnimation', true));
    _firstEntries = false;
    _recomputeMetrics();
    requestFrame();
  }

  final images = <String, ui.Image?>{};
  final _imageRequests = <String>{};
  int _imageGeneration = -1;

  /// Starts loading the picture of every entry (once per icon generation and size).
  void ensureImages(double devicePixelRatio) {
    if (_imageGeneration != state.iconGeneration) {
      _imageGeneration = state.iconGeneration;
      images.clear();
      _imageRequests.clear();
    }
    final px = (gp.iconSize * gp.maxScale * devicePixelRatio).ceil().clamp(32, 256);
    for (final e in entries) {
      final app = e.app;
      if (app == null || e.kind == DockEntryKind.separator || !_imageRequests.add('${e.key}|$px')) continue;
      final special = e.kind == DockEntryKind.start || e.kind == DockEntryKind.launchpad;
      state.iconBytes(app, px, fallback: !special).then((bytes) async {
        if (bytes == null) {
          images[e.key] = null;
          return;
        }
        final codec = await ui.instantiateImageCodec(bytes, targetWidth: px);
        final frame = await codec.getNextFrame();
        images[e.key] = frame.image;
        notifyListeners();
      });
    }
  }

  void _recomputeMetrics({bool force = false}) {
    final shape = motion.metricsShape();
    if (!force && shape == _shape) return;
    _shape = shape;
    // The window is sized for a few icons more than there are, and keeps its size while apps come and go: a resize is a jump in the
    // native window that the painted plate cannot follow, so opening or closing an app must not cause one. It grows when the icons
    // come within one slot of its edge and shrinks only after the row has become much shorter.
    final count = math.max(1, shape.$1);
    final resize = force || _capacity == 0 || count + 1 > _capacity || count + 9 < _capacity;
    if (resize) _capacity = count + 4;
    if (resize) metrics = WindowMetrics.compute(gp, _capacity, shape.$2);
    zones = WindowMetrics.compute(gp, count, shape.$2, viewportWidth: metrics.windowWidth);
    if (resize) onSizeChanged?.call(windowSize);
  }

  // ------------------------------------------------------------------ frame loop

  void requestFrame() => onNeedFrame?.call();

  /// Advances every animation by [dt] seconds. True while something is still moving.
  /// Counts the frames the dock advanced (read by the diagnostic frame log).
  static int ticks = 0;

  bool tick(double dt) {
    ticks++;
    final a = mag.advance(dt, responsiveness: _i('Responsiveness', 10), animationSpeed: _i('AnimationSpeed', 6), smoothness: _i('Smoothness', 5));
    final b = motion.advance(dt, iconAnimation: _b('IconAnimation', true), iconAnimationMs: _i('IconAnimationMs', 240));
    final c = _stepAutoHide(dt);
    final d = _stepLabel(dt);
    var pulsing = false;
    for (final k in pulses.keys.toList()) {
      final t = pulses[k]! + dt;
      if (t > .32) {
        pulses.remove(k);
      } else {
        pulses[k] = t;
        pulsing = true;
      }
    }
    _recomputeMetrics();
    DockLayout.compute(gp, metrics, motion.items, mag.cursorX, mag.zoom, layout);
    notifyListeners();
    return a || b || c || d || pulsing || dragging;
  }

  bool get effectiveAutoHide => _b('AutoHide', false) || concealed;

  bool _stepAutoHide(double dt) {
    var forced = suspended || userHidden || (concealed && !peek);
    final busy = !forced && (mag.hovering || dragging || menuOpen || kbMode || previewInside);
    idleFor = busy ? 0 : idleFor + dt;
    if (peek && !busy && idleFor > .55) {
      peek = false;
      forced = suspended || userHidden || concealed;
    }
    final target = forced || (effectiveAutoHide && !busy && idleFor > .55) ? 1.0 : 0.0;
    final speed = 1.2 + _i('AnimationSpeed', 6) * 0.3;
    final before = hide;
    hide = target > hide ? math.min(target, hide + dt * speed) : math.max(target, hide - dt * speed * 1.4);
    return hide != target || before != hide || (effectiveAutoHide && !busy && target == 0 && idleFor < .56);
  }

  bool _stepLabel(double dt) {
    final show = hoverIndex >= 0 && hoverIndex < motion.items.length && _b('ShowLabels', true) && !dragging && !menuOpen && !previewActive && mag.hovering;
    final target = show ? 1.0 : 0.0;
    final before = labelAlpha;
    labelAlpha = target > labelAlpha ? math.min(1, labelAlpha + dt / .12) : math.max(0, labelAlpha - dt / .10);
    if (show) labelKey = motion.items[hoverIndex].key;
    return labelAlpha != before && labelAlpha > 0 && labelAlpha < 1 || (labelAlpha != target);
  }

  /// Fully slid away: the controller takes the window off the screen and reveals it from the edge.
  bool get isHidden => hide >= 1;

  // ------------------------------------------------------------------ pointer

  (double a, double c) _logical(Offset p) => axis.logical(p);

  void pointerMoved(Offset p) {
    if (menuOpen || suspended || userHidden || (concealed && !peek)) return;
    final (a, c) = _logical(p);
    pointerInside = true;
    if (!mag.hovering && zones.enterZone.contains(Offset(a, c)) && (hide < .5 || _b('RevealOnEdgeHover', true))) {
      mag.hovering = true;
      mag.setCursor(a, snap: mag.zoom <= 0.001);
    } else if (mag.hovering) {
      if (!dragging && !zones.keepZone.contains(Offset(a, c)) && !previewInside) {
        endHover();
      } else {
        mag.setCursor(a);
      }
    }
    final hit = DockLayout.hitTest(motion.items, gp, metrics, a, c);
    if (hit != hoverIndex) {
      hoverIndex = hit;
      _restartDwell();
    }
    if (pressIndex >= 0 && !dragging && (p - pressPos).distance > 8) _tryBeginDrag();
    if (dragging) _updateDrag(a);
    requestFrame();
  }

  void pointerLeft() {
    pointerInside = false;
    if (!dragging && !kbMode && !previewInside) endHover();
    requestFrame();
  }

  void endHover() {
    mag.hovering = false;
    hoverIndex = -1;
    _dwell?.cancel();
    onPreviewHide?.call();
    requestFrame();
  }

  void _restartDwell() {
    _dwell?.cancel();
    onPreviewHide?.call();
    if (hoverIndex < 0 || !_b('ShowWindowPreviews', true)) return;
    final it = motion.items[hoverIndex];
    if (it.entry.kind != DockEntryKind.app || it.entry.windows.isEmpty) return;
    _dwell = Timer(Duration(milliseconds: _i('PreviewDelayMs', 750)), () {
      if (hoverIndex < 0 || hoverIndex >= motion.items.length || dragging || menuOpen) return;
      final cur = motion.items[hoverIndex];
      if (cur.key != it.key) return;
      onPreview?.call(cur.entry, axis.rect(cur.x, cur.top, cur.x + cur.size, cur.bottom));
    });
  }

  void pointerDown(Offset p, int buttons) {
    if (menuOpen || suspended || userHidden) return;
    final (a, c) = _logical(p);
    pressPos = p;
    pressIndex = _hitWithSeparators(a, c);
    _dwell?.cancel();
    onPreviewHide?.call();
  }

  int _hitWithSeparators(double a, double c) {
    for (var i = 0; i < motion.items.length; i++) {
      final it = motion.items[i];
      if (it.isSeparator && it.entry.pinned && a >= it.x - 6 && a <= it.x + it.size + 6 && c >= metrics.plateTop && c <= metrics.plateBottom) return i;
    }
    return DockLayout.hitTest(motion.items, gp, metrics, a, c);
  }

  /// [button]: kPrimaryButton, kMiddleMouseButton or kSecondaryButton.
  void pointerUp(Offset p, int button) {
    final (a, c) = _logical(p);
    final pressed = pressIndex;
    pressIndex = -1;
    final hit = DockLayout.hitTest(motion.items, gp, metrics, a, c);
    if (button == kSecondaryButton) {
      final i = _hitWithSeparators(a, c);
      onContextMenu?.call(i >= 0 && i < motion.items.length ? motion.items[i].entry : null, p);
      return;
    }
    if (button == kMiddleMouseButton) {
      if (hit >= 0 && motion.items[hit].entry.kind == DockEntryKind.app) onActivate?.call(motion.items[hit].entry, newInstance: true);
      return;
    }
    if (dragging) {
      _endDrag(c);
      return;
    }
    if (pressed >= 0 && hit == pressed && hit < motion.items.length) {
      final it = motion.items[hit];
      pulses[it.key] = 0;
      onActivate?.call(it.entry, newInstance: false);
      requestFrame();
    }
  }

  void pointerCancelled() {
    if (dragging) _endDrag(null);
    pressIndex = -1;
  }

  // ------------------------------------------------------------------ drag to reorder / unpin

  void _tryBeginDrag() {
    if (pressIndex < 0 || pressIndex >= motion.items.length) return;
    final it = motion.items[pressIndex];
    if (!it.entry.pinned || (it.entry.kind != DockEntryKind.app && it.entry.kind != DockEntryKind.separator)) return;
    dragging = true;
    dragKey = it.key;
    _dwell?.cancel();
    onPreviewHide?.call();
  }

  int _lastMoveTarget = -1;

  void _updateDrag(double a) {
    final drag = motion.items.where((i) => i.key == dragKey).firstOrNull;
    if (drag == null) return;
    drag.dragX = a.clamp(0, metrics.windowWidth);
    final pinned = motion.items.where((i) => !i.leaving && i.entry.pinned).toList();
    final current = pinned.indexOf(drag);
    final target = pinned.where((i) => i != drag && i.baseX + (i.isSeparator ? gp.separatorWidth : gp.iconSize) * .5 < a).length;
    if (current >= 0 && target != current && target != _lastMoveTarget) {
      _lastMoveTarget = target;
      onMovePinned?.call(drag.key, target);
    }
    if (target == current) _lastMoveTarget = -1;
  }

  void _endDrag(double? releaseC) {
    final drag = motion.items.where((i) => i.key == dragKey).firstOrNull;
    final key = dragKey;
    dragging = false;
    dragKey = null;
    _lastMoveTarget = -1;
    if (drag != null) {
      drag.slide = drag.x - drag.baseX;
      drag.dragX = null;
      // dragged out of the dock: unpin (a running app stays as a running icon)
      if (releaseC != null && releaseC < metrics.plateTop - 50 && key != null) onUnpin?.call(key);
    }
    requestFrame();
  }

  // ------------------------------------------------------------------ keyboard mode

  void enterKeyboardMode() {
    kbMode = true;
    final first = motion.items.indexWhere((i) => !i.isSeparator && !i.leaving);
    kbIndex = kbIndex >= 0 && kbIndex < motion.items.length ? kbIndex : first;
    _syncKb();
  }

  void leaveKeyboardMode() {
    kbMode = false;
    kbIndex = -1;
    endHover();
    onLeftKbMode?.call();
  }

  void _syncKb() {
    if (kbIndex < 0 || kbIndex >= motion.items.length) return;
    mag.hovering = true;
    hoverIndex = kbIndex;
    mag.setCursor(motion.items[kbIndex].baseX + gp.iconSize * .5, snap: mag.zoom <= .001);
    requestFrame();
  }

  KeyEventResult key(KeyEvent event) {
    if (!kbMode || event is KeyUpEvent) return KeyEventResult.ignored;
    final k = event.logicalKey;
    int step(int dir) {
      var i = kbIndex;
      do {
        i += dir;
      } while (i >= 0 && i < motion.items.length && (motion.items[i].isSeparator || motion.items[i].leaving));
      return i >= 0 && i < motion.items.length ? i : kbIndex;
    }

    if (k == LogicalKeyboardKey.escape) {
      leaveKeyboardMode();
    } else if (k == LogicalKeyboardKey.arrowLeft || k == LogicalKeyboardKey.arrowUp) {
      kbIndex = step(-1);
      _syncKb();
    } else if (k == LogicalKeyboardKey.arrowRight || k == LogicalKeyboardKey.arrowDown) {
      kbIndex = step(1);
      _syncKb();
    } else if (k == LogicalKeyboardKey.enter || k == LogicalKeyboardKey.space) {
      if (kbIndex >= 0 && kbIndex < motion.items.length) {
        final e = motion.items[kbIndex].entry;
        leaveKeyboardMode();
        onActivate?.call(e, newInstance: false);
      }
    } else {
      return KeyEventResult.ignored;
    }
    return KeyEventResult.handled;
  }

  void cancelTimers() => _dwell?.cancel();

  @override
  void dispose() {
    cancelTimers();
    super.dispose();
  }
}

/// The dock window's content: the plate, the icons, labels and indicators, all drawn by one painter.
class DockView extends StatefulWidget {
  const DockView({super.key, required this.model});
  final DockModel model;

  @override
  State<DockView> createState() => _DockViewState();
}

class _DockViewState extends State<DockView> with SingleTickerProviderStateMixin {
  late final Ticker _ticker = createTicker(_onTick);
  Duration _last = Duration.zero;
  final _focus = FocusNode();

  DockModel get m => widget.model;

  @override
  void initState() {
    super.initState();
    m.onNeedFrame = _start;
    m.state.addListener(_onState);
    m.updateSettings(m.state.settings);
    m.addListener(_noop);
  }

  void _noop() {}

  @override
  void dispose() {
    m.removeListener(_noop);
    m.state.removeListener(_onState);
    m.onNeedFrame = null;
    m.cancelTimers();
    _wait?.cancel();
    _ticker.dispose();
    _focus.dispose();
    super.dispose();
  }

  Map<String, Object?>? _lastSettings;

  void _onState() {
    // Only the settings this window draws from restart the layout; everything else is ignored.
    final s = m.state.settings;
    if (!identical(s, _lastSettings)) {
      _lastSettings = s;
      m.updateSettings(s);
    }
  }

  Timer? _wait;

  void _start() {
    if (_wait != null) return; // the next frame is already on its way (see _onTick)
    if (!_ticker.isActive) {
      _last = Duration.zero;
      _ticker.start();
    }
  }

  void _resume() {
    _wait = null;
    if (!_ticker.isActive) _ticker.start();
  }

  void _onTick(Duration elapsed) {
    // "Animation frame limit". A tick that comes sooner than the limit allows is not just skipped: the ticker is stopped until the
    // rest of the interval has passed, because as long as it runs the engine draws a frame on every refresh of the display.
    // Frame timestamps run on across restarts of the ticker, and they are real time: dt must not be shorter than what has passed.
    final now = SchedulerBinding.instance.currentFrameTimeStamp;
    final interval = 1e6 / ((m.state.settings['AnimationFps'] as num?)?.toInt() ?? 60);
    if (_last != Duration.zero) {
      final since = (now - _last).inMicroseconds;
      if (since < interval * .85) {
        _ticker.stop();
        _wait = Timer(Duration(microseconds: (interval - since).round()), _resume);
        return;
      }
    }
    final dt = _last == Duration.zero ? 1 / 60 : (now - _last).inMicroseconds / 1e6;
    _last = now;
    final moving = m.tick(dt);
    if (!moving && _ticker.isActive) _ticker.stop();
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    m.ensureImages(MediaQuery.devicePixelRatioOf(context));
    return Focus(
      focusNode: _focus,
      onKeyEvent: (node, event) => m.key(event),
      child: MouseRegion(
        onHover: (e) => m.pointerMoved(e.localPosition),
        onExit: (_) => m.pointerLeft(),
        cursor: SystemMouseCursors.basic,
        child: Listener(
          behavior: HitTestBehavior.translucent,
          onPointerDown: (e) {
            _lastButtons = e.buttons;
            m.pointerDown(e.localPosition, e.buttons);
          },
          onPointerMove: (e) => m.pointerMoved(e.localPosition),
          onPointerUp: (e) => m.pointerUp(e.localPosition, _buttonOf()),
          onPointerCancel: (_) => m.pointerCancelled(),
          child: BlurScope(
            child: Builder(
              builder: (context) {
                final blur = BlurScope.maybeOf(context);
                return CustomPaint(
                  size: Size.infinite,
                  painter: DockPainter(m, cs, _settingsView(m.state), onPlate: (rect, radius) => blur?.setExtra(this, rect, radius)),
                  isComplex: true,
                );
              },
            ),
          ),
        ),
      ),
    );
  }

  int _lastButtons = 0;

  int _buttonOf() {
    // After the release no button is down: use the one that was pressed.
    final b = _lastButtons;
    return b == kSecondaryButton || b == kMiddleMouseButton ? b : kPrimaryButton;
  }

  Map<String, Object?> _settingsView(AppState s) => s.settings;
}

/// How far the plate of a dock touching the screen's edge flares out under each end.
const double flushWing = 12;

/// Paints the dock. Settings and colours are read at paint time, so a theme change repaints at once.
class DockPainter extends CustomPainter {
  DockPainter(this.m, this.cs, this.settings, {this.onPlate}) : super(repaint: m);
  final void Function(Rect plate, double radius)? onPlate;
  final DockModel m;
  final ColorScheme cs;
  final Map<String, Object?> settings;

  bool _b(String k, bool f) => settings[k] is bool ? settings[k] as bool : f;
  int _i(String k, int f) => (settings[k] as num?)?.toInt() ?? f;

  Color get plateColor {
    final hex = '${settings['BackgroundColor'] ?? '1E1E1E'}'.toUpperCase();
    final opacity = (_i('Opacity', 55) / 100).clamp(0.0, 1.0);
    // The default colour means "follow the palette"; a colour the user chose is used as it is.
    final base = hex == '1E1E1E' ? cs.surface : Color(0xFF000000 | (int.tryParse(hex, radix: 16) ?? 0x1E1E1E));
    return base.withValues(alpha: opacity);
  }

  @override
  void paint(Canvas canvas, Size size) {
    final axis = m.axis;
    final off = axis.toEdge(Magnification.smooth01(m.hide) * (m.metrics.windowHeight - m.metrics.plateTop + 6 + math.max(0, _i('BottomOffset', 10) - 26)));
    canvas.save();
    canvas.translate(off.dx, off.dy);

    final res = m.layout;
    final radius = _i('CornerRadius', 22).toDouble();
    final plate = RRect.fromRectAndRadius(axis.rect(res.plateLeft, m.metrics.plateTop, res.plateRight, m.metrics.plateBottom), Radius.circular(radius));
    // Touching the screen's edge (distance 0) the plate is rounded only on the free side and flares out into the edge on both ends
    // (the reference); anywhere else it is a floating rounded plate with a shadow and a thin border.
    final flush = _i('BottomOffset', 10) <= 0;
    onPlate?.call(plate.outerRect.shift(off), radius);
    if (flush) {
      final top = math.min(radius, m.gp.dockHeight / 2);
      canvas.drawPath(
        _flushPlate(axis, res.plateLeft, m.metrics.plateTop, res.plateRight, m.metrics.plateBottom, top, math.min(flushWing, m.gp.dockHeight / 2)),
        Paint()..color = plateColor..isAntiAlias = true,
      );
    } else {
      if (_b('Shadow', true)) {
        _drawShadow(canvas, plate);
      }
      canvas.drawRRect(plate, Paint()..color = plateColor);
      if (_b('Border', true)) {
        canvas.drawRRect(plate.deflate(.5), Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = cs.onSurface.withValues(alpha: .08));
      }
    }

    final gp = m.gp;
    final style = '${settings['IndicatorStyle'] ?? 'Dot'}';
    for (final it in m.motion.items) {
      final pe = it.presence;
      if (pe <= 0.002) continue;
      if (it.isSeparator) {
        final r = axis.rect(it.x + it.size * .5 - .75, m.metrics.plateTop + m.gp.dockHeight * .5 - gp.iconSize * .275, it.x + it.size * .5 + .75, m.metrics.plateTop + m.gp.dockHeight * .5 + gp.iconSize * .275);
        canvas.drawRRect(RRect.fromRectAndRadius(r, const Radius.circular(.75)), Paint()..color = cs.onSurface.withValues(alpha: .35 * pe));
        continue;
      }
      var size = it.size;
      final pulse = m.pulses[it.key];
      var lift = 0.0;
      if (pulse != null) {
        final t = (pulse / .32).clamp(0.0, 1.0);
        final bump = math.sin(t * math.pi);
        size *= 1 + .12 * bump;
        lift = 6 * bump;
      }
      final top = it.bottom - size - lift;
      final dst = axis.rect(it.x - (size - it.size) / 2, top, it.x - (size - it.size) / 2 + size, top + size);
      _drawIcon(canvas, it, dst, pe);
      _drawIndicator(canvas, it, axis, style, pe);
    }
    _drawLabel(canvas, axis);
    canvas.restore();
  }

  /// The plate of a dock that touches the screen's edge: rounded corners [rad] on the free side, and under both ends a concave fillet
  /// of [wing] that joins the plate to the edge. Built in the dock's own (along, across) coordinates and mapped to the window.
  static Path _flushPlate(DockAxis axis, double l, double t, double r, double b, double rad, double wing) {
    const k = .5523;
    Offset p(double a, double c) => axis.point(a, c);
    return Path()
      ..moveTo(p(l - wing, b).dx, p(l - wing, b).dy)
      ..cubicTo(p(l - wing + k * wing, b).dx, p(l - wing + k * wing, b).dy, p(l, b - wing + k * wing).dx, p(l, b - wing + k * wing).dy, p(l, b - wing).dx, p(l, b - wing).dy)
      ..lineTo(p(l, t + rad).dx, p(l, t + rad).dy)
      ..cubicTo(p(l, t + rad - k * rad).dx, p(l, t + rad - k * rad).dy, p(l + rad - k * rad, t).dx, p(l + rad - k * rad, t).dy, p(l + rad, t).dx, p(l + rad, t).dy)
      ..lineTo(p(r - rad, t).dx, p(r - rad, t).dy)
      ..cubicTo(p(r - rad + k * rad, t).dx, p(r - rad + k * rad, t).dy, p(r, t + rad - k * rad).dx, p(r, t + rad - k * rad).dy, p(r, t + rad).dx, p(r, t + rad).dy)
      ..lineTo(p(r, b - wing).dx, p(r, b - wing).dy)
      ..cubicTo(p(r, b - wing + k * wing).dx, p(r, b - wing + k * wing).dy, p(r + wing - k * wing, b).dx, p(r + wing - k * wing, b).dy, p(r + wing, b).dx, p(r + wing, b).dy)
      ..close();
  }

  /// A soft shadow made of a few translucent rings: a blur filter on a plate that changes size every frame was the most expensive part of
  /// painting the dock.
  void _drawShadow(Canvas canvas, RRect plate) {
    final paint = Paint()..color = const Color(0x06000000);
    final base = plate.shift(const Offset(0, 4));
    for (var i = 15; i >= 1; i--) {
      canvas.drawRRect(base.inflate(i * 1.5), paint);
    }
  }

  void _drawIcon(Canvas canvas, DockItem it, Rect dst, double presence) {
    // Every icon sits on its own softly lighter rounded tile (the reference), which grows and moves with the icon. The tile reaches
    // out over half of the gap between icons, leaving a thin gap between tiles.
    final tile = dst.inflate(math.max(2.0, (m.gp.gap - 4) / 2));
    canvas.drawRRect(RRect.fromRectAndRadius(tile, Radius.circular(tile.width * .28)), Paint()..color = cs.surfaceContainerHigh.withValues(alpha: presence));
    final image = m.images[it.key];
    final paint = Paint()..filterQuality = FilterQuality.medium..color = Color.fromRGBO(255, 255, 255, presence.clamp(0.0, 1.0));
    if (image != null) {
      canvas.drawImageRect(image, Rect.fromLTWH(0, 0, image.width.toDouble(), image.height.toDouble()), dst, paint);
      return;
    }
    switch (it.entry.kind) {
      case DockEntryKind.start:
        _drawStart(canvas, dst, presence);
      case DockEntryKind.launchpad:
        _drawLaunchpad(canvas, dst, presence);
      default:
        // The picture is still loading: a slightly brighter tile of the palette, never an empty hole.
        canvas.drawRRect(RRect.fromRectAndRadius(dst.deflate(dst.width * .06), Radius.circular(dst.width * .22)), Paint()..color = cs.surfaceContainerHighest.withValues(alpha: .85 * presence));
    }
  }

  void _drawStart(Canvas canvas, Rect r, double pe) {
    final s = r.width * .5;
    final gap = r.width * .05;
    final o = r.center - Offset(s / 2 + gap / 2, s / 2 + gap / 2);
    final paint = Paint()..color = cs.primary.withValues(alpha: pe);
    for (var i = 0; i < 4; i++) {
      final cell = Rect.fromLTWH(o.dx + (i % 2) * (s / 2 + gap), o.dy + (i ~/ 2) * (s / 2 + gap), s / 2, s / 2);
      canvas.drawRRect(RRect.fromRectAndRadius(cell, Radius.circular(s * .08)), paint);
    }
  }

  void _drawLaunchpad(Canvas canvas, Rect r, double pe) {
    final style = '${settings['LaunchpadIconStyle'] ?? 'WhiteSquares'}';
    if (style != 'WhiteSquares') {
      canvas.drawRRect(RRect.fromRectAndRadius(r.deflate(r.width * .04), Radius.circular(r.width * .24)),
          Paint()..color = (style == 'ColorOnWhite' ? Colors.white : const Color(0xFF263238)).withValues(alpha: pe));
    }
    final cell = r.width * .17, gap = r.width * .07;
    final total = cell * 3 + gap * 2;
    final o = r.center - Offset(total / 2, total / 2);
    const colors = [Color(0xFFEF5350), Color(0xFFFFA726), Color(0xFFFFEE58), Color(0xFF66BB6A), Color(0xFF26C6DA), Color(0xFF42A5F5), Color(0xFF7E57C2), Color(0xFFEC407A), Color(0xFF8D6E63)];
    for (var i = 0; i < 9; i++) {
      final cellRect = Rect.fromLTWH(o.dx + (i % 3) * (cell + gap), o.dy + (i ~/ 3) * (cell + gap), cell, cell);
      final color = style == 'WhiteSquares' ? Colors.white : colors[i];
      canvas.drawRRect(RRect.fromRectAndRadius(cellRect, Radius.circular(cell * .3)), Paint()..color = color.withValues(alpha: pe));
    }
  }

  void _drawIndicator(Canvas canvas, DockItem it, DockAxis axis, String style, double pe) {
    if (it.runningAlpha <= 0.01 || !_b('ShowActiveIndicator', true) && it.activeAlpha > 0) return;
    final a = it.x + it.size / 2;
    final c = m.metrics.plateBottom - 5.5;
    final active = it.activeAlpha;
    final color = Color.lerp(cs.onSurface.withValues(alpha: .65), cs.primary, active)!.withValues(alpha: it.runningAlpha * pe);
    final scale = 1 + .25 * active;
    if (style == 'Bar') {
      final w = 12 * scale, h = 3 * scale;
      canvas.drawRRect(RRect.fromRectAndRadius(axis.rect(a - w / 2, c - h / 2, a + w / 2, c + h / 2), Radius.circular(h / 2)), Paint()..color = color);
    } else {
      canvas.drawCircle(axis.point(a, c), 2.5 * scale, Paint()..color = color);
    }
  }

  static TextPainter? _labelPainter;
  static String _labelText = '';
  static int _labelStep = -1;
  static Color? _labelColor;

  void _drawLabel(Canvas canvas, DockAxis axis) {
    if (m.labelAlpha <= 0.01 || m.labelKey == null) return;
    final it = m.motion.items.where((i) => i.key == m.labelKey).firstOrNull;
    if (it == null || it.entry.name.isEmpty) return;
    final text = it.entry.kind == DockEntryKind.app ? it.entry.name : tr(it.entry.name);
    final step = (m.labelAlpha * 16).round();
    var painter = _labelPainter;
    if (painter == null || _labelText != text || _labelStep != step || _labelColor != cs.onSurface) {
      // Laying out the text is repeated only when the text or the fade has moved a step, not on every frame of the hover.
      painter = TextPainter(
        text: TextSpan(text: text, style: TextStyle(fontFamily: fontFamily, fontFamilyFallback: fontFallback, fontSize: 11.5, color: cs.onSurface.withValues(alpha: step / 16), fontWeight: FontWeight.w500)),
        textDirection: TextDirection.ltr,
        maxLines: 1,
        ellipsis: '…',
      )..layout(maxWidth: 260);
      _labelPainter = painter;
      _labelText = text;
      _labelStep = step;
      _labelColor = cs.onSurface;
    }
    final w = painter.width + 20, h = painter.height + 10;
    final a = it.x + it.size / 2;
    final c = it.top - 10 - h;
    final rect = axis.rect(a - w / 2, c, a + w / 2, c + h);
    canvas.drawRRect(RRect.fromRectAndRadius(rect, const Radius.circular(10)), Paint()..color = cs.surfaceContainerHighest.withValues(alpha: .96 * m.labelAlpha));
    painter.paint(canvas, rect.topLeft + const Offset(10, 5));
  }

  @override
  bool shouldRepaint(DockPainter old) => old.cs != cs || !identical(old.settings, settings);
}
