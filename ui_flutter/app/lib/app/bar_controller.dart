import 'dart:async';
import 'dart:math' as math;

import 'package:fd_ipc/client.dart';
import 'package:flutter/widgets.dart';

import '../core/app_state.dart';
import '../core/native.dart';
import '../windows/bar/bar_view.dart';
import 'controller.dart';
import 'host.dart';

class _Bar {
  _Bar(this.model, this.handle, this.monitor);
  final BarModel model;
  final WindowHandle handle;
  MonitorInfo monitor;
  bool fullscreen = false;

  /// Whether the native window is on screen (it is hidden once the bar has slid away, so it never blocks clicks).
  bool windowShown = false;
  bool appBarOn = false;
  int slideGeneration = 0;
}

/// One top bar window per monitor, kept in line with the settings, the monitors, fullscreen windows and the open menus.
class BarController {
  BarController(this.app);
  final AppController app;
  AppState get state => app.state;

  final _bars = <String, _Bar>{};
  final _creating = <String, Future<_Bar>>{};
  Timer? _edgeTimer, _syncTimer;
  String _signature = '';
  StreamSubscription<FdEvent>? _events;
  bool _syncing = false, _again = false;

  void start() {
    state.addListener(_onState);
    _events = state.eventsOther.listen((e) {
      if (e.name == 'display.changed') _scheduleSync(force: true);
      if (e.name == 'fullscreen.changed') _applyFullscreen((e.data as Map).cast<String, Object?>());
    });
    app.popupVisible.addListener(_onPopup);
    _scheduleSync(force: true);
  }

  void dispose() {
    state.removeListener(_onState);
    app.popupVisible.removeListener(_onPopup);
    _events?.cancel();
    _edgeTimer?.cancel();
    _syncTimer?.cancel();
  }

  String _settingsSignature() => [
        'TopBarEnabled', 'TopBarAutoHide', 'TopBarReserveSpace', 'MonitorMode', 'TopBarHideDelayMs',
      ].map((k) => '${state.settings[k]}').join('|');

  void _onState() {
    if (!state.connected) return;
    final sig = _settingsSignature();
    if (sig != _signature) _scheduleSync();
  }

  void _scheduleSync({bool force = false}) {
    _syncTimer?.cancel();
    _syncTimer = Timer(Duration(milliseconds: force ? 350 : 120), _sync);
  }

  Future<void> _sync() async {
    if (_syncing) {
      _again = true;
      return;
    }
    _syncing = true;
    try {
      await _syncCore();
    } catch (_) {
      // Windows can refuse (a monitor just went away): the next display event syncs again.
    } finally {
      _syncing = false;
      if (_again) {
        _again = false;
        unawaited(_sync());
      }
    }
  }

  Future<void> _syncCore() async {
    _signature = _settingsSignature();
    final enabled = state.setting<bool>('TopBarEnabled', true);
    final autoHide = state.setting<bool>('TopBarAutoHide', false);
    final reserve = state.setting<bool>('TopBarReserveSpace', true);
    var monitors = await native.monitors();
    if (state.setting<String>('MonitorMode', 'Primary') == 'Primary') {
      monitors = [monitors.firstWhere((m) => m.primary, orElse: () => monitors.first)];
    }
    final wanted = <String>{};
    if (enabled) {
      for (final mon in monitors) {
        final key = '${mon.x},${mon.y}';
        wanted.add(key);
        final bar = await (_creating[key] ??= _create(key, mon));
        bar.monitor = mon;
        final w = mon.w / mon.scale;
        bar.model
          ..origin = Offset(mon.x.toDouble(), mon.y.toDouble())
          ..scale = mon.scale
          ..widthDip = w
          ..changed();
        await bar.handle.setBounds(x: mon.x, y: mon.y, width: w, height: barWindowHeight);
        if (!bar.model.hidden.value && !bar.windowShown) await _showWindow(bar);
        final reserveNow = reserve && !autoHide;
        if (reserveNow) {
          await native.appBar(bar.handle.viewId, edge: 1, size: (barWindowHeight * mon.scale).ceil(), monitor: mon);
          bar.appBarOn = true;
        } else if (bar.appBarOn) {
          await native.removeAppBar(bar.handle.viewId);
          bar.appBarOn = false;
        }
        if (!autoHide && !bar.fullscreen) _target(bar, hidden: false);
      }
    }
    for (final entry in _bars.entries.where((e) => !wanted.contains(e.key))) {
      final bar = entry.value;
      if (bar.appBarOn) {
        await native.removeAppBar(bar.handle.viewId);
        bar.appBarOn = false;
      }
      if (bar.windowShown) {
        bar.windowShown = false;
        await bar.handle.hide();
      }
    }
    final needsEdge = enabled && (autoHide || _bars.values.any((b) => b.fullscreen));
    if (needsEdge && _edgeTimer == null) {
      _edgeTimer = Timer.periodic(const Duration(milliseconds: 100), (_) => _edge());
    } else if (!needsEdge) {
      _edgeTimer?.cancel();
      _edgeTimer = null;
    }
    if (enabled && autoHide) {
      for (final bar in _bars.values.where((b) => wanted.contains('${b.monitor.x},${b.monitor.y}'))) {
        if (!bar.model.hover) _target(bar, hidden: true);
      }
    }
    // Ask once, then keep up to date by event.
    try {
      _applyFullscreen(((await state.call('fullscreen.get')) as Map).cast<String, Object?>());
    } catch (_) {}
  }

  Future<_Bar> _create(String key, MonitorInfo mon) async {
    final model = BarModel(key);
    final handle = await app.host.create(
      WindowSpec(
        kind: 'bar', x: mon.x, y: mon.y, width: mon.w / mon.scale, height: barWindowHeight, topmost: true, noActivate: true,
        title: 'FloatingDock menu bar',
      ),
      (context) => BarView(model: model),
    );
    final bar = _Bar(model, handle, mon);
    model.onMenu = (kind, anchor) => unawaited(app.openPopup(kind, anchor, alignLeft: kind == 'main'));
    model.onSearch = () => unawaited(app.toggleSearch());
    model.onOpenUri = (uri) => state.call('shell.execute', {'uri': uri}).catchError((_) => null);
    model.onPointerEnter = () => _reveal(bar);
    _bars[key] = bar;
    return bar;
  }

  Future<void> _showWindow(_Bar bar) async {
    bar.windowShown = true;
    await bar.handle.show(activate: false);
  }

  void _reveal(_Bar bar) {
    if (bar.fullscreen) return;
    bar.model.lastHover = DateTime.now();
    _target(bar, hidden: false);
  }

  void _target(_Bar bar, {required bool hidden}) {
    hidden |= bar.fullscreen;
    if (bar.model.hidden.value == hidden) return;
    final generation = ++bar.slideGeneration;
    if (!hidden) {
      if (!bar.windowShown) unawaited(_showWindow(bar));
      bar.model.hidden.value = false;
      return;
    }
    // Slide up, then take the window off the screen so it cannot block what is under it.
    app.closePopup();
    bar.model.hidden.value = true;
    final ms = state.setting<int>('TopBarHideMs', 220);
    Future<void>.delayed(Duration(milliseconds: ms + 60), () {
      if (generation == bar.slideGeneration && bar.model.hidden.value && bar.windowShown) {
        bar.windowShown = false;
        bar.handle.hide();
      }
    });
  }

  Future<void> _edge() async {
    final autoHide = state.setting<bool>('TopBarAutoHide', false);
    final delay = Duration(milliseconds: state.setting<int>('TopBarHideDelayMs', 700));
    Offset? cursor;
    for (final bar in _bars.values) {
      if (!state.setting<bool>('TopBarEnabled', true)) return;
      if (bar.fullscreen) {
        _target(bar, hidden: true);
        continue;
      }
      if (!autoHide) continue;
      if (bar.model.openKind != null || app.openPopupKind != null && bar.model.openKind != null) continue;
      cursor ??= await native.cursor();
      final mon = bar.monitor;
      final inset = (barSideInset * mon.scale).round();
      final reach = bar.model.hidden.value ? 2 : ((barTopInset + barHeight) * mon.scale).round();
      final over = cursor.dx >= mon.x + inset && cursor.dx < mon.x + mon.w - inset && cursor.dy >= mon.y && cursor.dy < mon.y + reach;
      if (over) {
        _reveal(bar);
      } else if (!bar.model.hover && DateTime.now().difference(bar.model.lastHover) > delay) {
        _target(bar, hidden: true);
      }
    }
  }

  void _applyFullscreen(Map<String, Object?> data) {
    final list = ((data['monitors'] as List?) ?? const []).cast<Map>();
    var changed = false;
    for (final bar in _bars.values) {
      final entry = list.where((m) => m['x'] == bar.monitor.x && m['y'] == bar.monitor.y).firstOrNull;
      final full = entry?['fullscreen'] == true;
      if (bar.fullscreen == full) continue;
      bar.fullscreen = full;
      changed = true;
      if (full) {
        app.closePopup();
        _target(bar, hidden: true);
      } else if (!state.setting<bool>('TopBarAutoHide', false)) {
        _target(bar, hidden: false);
      }
    }
    if (changed) _scheduleSync();
  }

  void _onPopup() {
    final kind = app.popupVisible.value ? app.openPopupKind : null;
    for (final bar in _bars.values) {
      if (bar.model.openKind != kind) {
        bar.model.openKind = kind;
        bar.model.lastHover = DateTime.now();
        bar.model.changed();
      }
    }
  }
}

extension _FirstOrNull<T> on Iterable<T> {
  T? get firstOrNull => isEmpty ? null : first;
}

/// Used by the layout maths in tests.
int reservedBarPixels(double scale) => math.max(1, (barWindowHeight * scale).ceil());
