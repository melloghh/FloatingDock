import 'dart:async';
import 'dart:math' as math;

import 'package:fd_ipc/client.dart';
import 'package:flutter/material.dart';

import '../core/app_state.dart';
import '../core/native.dart';
import '../windows/dock/dock_math.dart';
import '../windows/dock/dock_view.dart';
import '../windows/dock/preview_view.dart';
import '../windows/popup/context_menu.dart';
import 'controller.dart';
import 'host.dart';

class _Dock {
  _Dock(this.model, this.handle, this.monitor);
  final DockModel model;
  final WindowHandle handle;
  MonitorInfo monitor;
  bool shown = false;
  bool concealed = false, blockReveal = false;
  bool appBarOn = false;
  Rect? screenPlate; // physical pixels
}

/// One dock window per monitor, placed on the chosen edge, fed by the engine's model of the dock, and the window previews.
class DockController {
  DockController(this.app);
  final AppController app;
  AppState get state => app.state;

  final _docks = <String, _Dock>{};
  final _creating = <String, Future<_Dock>>{};
  Timer? _syncTimer, _edgeTimer, _anchorTimer;
  StreamSubscription<FdEvent>? _events;
  String _signature = '';
  bool _syncing = false, _again = false, _userHidden = false, _suspended = false;
  List<DockEntry> _entries = const [];

  void start() {
    state.addListener(_onState);
    _events = state.eventsOther.listen((e) {
      switch (e.name) {
        case 'dock.entries':
          _setEntries(e.data as List);
        case 'space.changed':
          _applySpace((e.data as Map).cast<String, Object?>());
        case 'display.changed':
          _scheduleSync(force: true);
        case 'dock.hiddenChanged':
          _userHidden = (e.data as Map)['hidden'] == true;
          for (final d in _docks.values) {
            d.model.userHidden = _userHidden;
            _wake(d);
          }
          _scheduleSync();
      }
    });
    state.actions['dockSuspended'] = ([arg]) {
      _suspended = arg == 'true';
      for (final d in _docks.values) {
        d.model.suspended = _suspended;
        if (_suspended) d.model.endHover();
        _wake(d);
      }
    };
    state.actions['focusDock'] = ([_]) => unawaited(focusDock());
    app.popupVisible.addListener(_onPopup);
    _scheduleSync(force: true);
  }

  void dispose() {
    state.removeListener(_onState);
    app.popupVisible.removeListener(_onPopup);
    _events?.cancel();
    _syncTimer?.cancel();
    _edgeTimer?.cancel();
    _anchorTimer?.cancel();
    _previewHideTimer?.cancel();
  }

  // ------------------------------------------------------------------ sync

  String _settingsSignature() => [
        'Edge', 'MonitorMode', 'AutoHide', 'ReserveWorkArea', 'KeepDockInFullscreen', 'TaskbarMode', 'BottomOffset', 'DockHeight',
        'IconSize', 'IconSpacing', 'WidthMode', 'FixedWidth', 'Alignment', 'HorizontalOffset', 'MaxScalePercent', 'MagnificationRadius',
        'MagnificationEnabled', 'ShowLabels',
      ].map((k) => '${state.settings[k]}').join('|');

  void _onState() {
    if (!state.connected) return;
    final sig = _settingsSignature();
    if (sig != _signature) _scheduleSync();
  }

  void _scheduleSync({bool force = false}) {
    _syncTimer?.cancel();
    _syncTimer = Timer(Duration(milliseconds: force ? 350 : 80), _sync);
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
      // Windows can refuse while a monitor changes: the next display event syncs again.
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
    var monitors = await native.monitors();
    if (state.setting<String>('MonitorMode', 'Primary') == 'Primary') {
      monitors = [monitors.firstWhere((m) => m.primary, orElse: () => monitors.first)];
    }
    final wanted = <String>{};
    if (_entries.isEmpty) await _loadEntries();
    for (final mon in monitors) {
      final key = '${mon.x},${mon.y}';
      wanted.add(key);
      final d = await (_creating[key] ??= _create(key, mon));
      d.monitor = mon;
      d.model.userHidden = _userHidden;
      d.model.suspended = _suspended;
      await _reposition(d);
    }
    for (final e in _docks.entries.where((e) => !wanted.contains(e.key))) {
      final d = e.value;
      if (d.appBarOn) {
        await native.removeAppBar(d.handle.viewId);
        d.appBarOn = false;
      }
      if (d.shown) {
        d.shown = false;
        await d.handle.hide();
      }
    }
    await _reserve();
    final needEdge = _docks.values.any((d) => wanted.contains('${d.monitor.x},${d.monitor.y}'));
    if (needEdge && _edgeTimer == null) {
      _edgeTimer = Timer.periodic(const Duration(milliseconds: 100), (_) => _edge());
    }
    try {
      _applySpace(((await state.call('space.get')) as Map).cast<String, Object?>());
    } catch (_) {}
  }

  Future<void> _loadEntries() async {
    try {
      _setEntries(await state.call('dock.entries') as List);
    } catch (_) {}
  }

  void _setEntries(List data) {
    _entries = [for (final m in data.cast<Map>()) DockEntry.fromJson(m)];
    for (final d in _docks.values) {
      d.model.setEntries(_entries);
    }
    _scheduleAnchors();
  }

  Future<_Dock> _create(String key, MonitorInfo mon) async {
    final model = DockModel(state: state);
    model.setEntries(_entries);
    final size = model.windowSize;
    final handle = await app.host.create(
      WindowSpec(kind: 'dock', x: mon.x, y: mon.y, width: size.width, height: size.height, topmost: true, noActivate: true, title: 'FloatingDock'),
      (context) => DockView(model: model),
    );
    final d = _Dock(model, handle, mon);
    model
      ..onActivate = ((e, {bool newInstance = false}) => _activate(d, e, newInstance))
      ..onContextMenu = ((e, p) => unawaited(_contextMenu(d, e, p)))
      ..onMovePinned = ((key, index) => state.call('pinned.reorder', {'key': key, 'index': index}).catchError((_) => null))
      ..onUnpin = ((key) => state.call('pinned.remove', {'key': key}).catchError((_) => null))
      ..onPreview = ((e, rect) => unawaited(_showPreview(d, e, rect)))
      ..onPreviewHide = _scheduleHidePreview
      ..onSizeChanged = ((_) => _scheduleReposition(d))
      ..onLeftKbMode = (() => native.setNoActivate(handle.viewId, true));
    _docks[key] = d;
    model.addListener(() => _onModel(d));
    return d;
  }

  // ------------------------------------------------------------------ placement

  void _scheduleReposition(_Dock d) {
    _syncTimer?.cancel();
    _syncTimer = Timer(const Duration(milliseconds: 16), () => unawaited(_reposition(d)));
  }

  Future<void> _reposition(_Dock d) async {
    final s = state.settings;
    final mon = d.monitor;
    final scale = mon.scale;
    final m = d.model.metrics;
    final size = d.model.windowSize;
    final edge = '${s['Edge'] ?? 'Bottom'}';
    final vertical = edge != 'Bottom';
    final fixed = s['WidthMode'] == 'Fixed';
    final offset = (s['HorizontalOffset'] as num?)?.toDouble() ?? 0;
    final bottomOffset = (s['BottomOffset'] as num?)?.toDouble() ?? 10;
    final start = (vertical ? mon.y : mon.x).toDouble();
    final end = vertical ? mon.y + mon.h.toDouble() : mon.x + mon.w.toDouble();
    final left = m.restPlateLeft, right = m.restPlateRight;
    final along = switch ('${s['Alignment'] ?? 'Center'}') {
      'Left' when !fixed => start + (12 + offset - left) * scale,
      'Right' when !fixed => end + (-12 + offset - right) * scale,
      _ => (start + end) / 2 + (offset - (left + right) / 2) * scale,
    };
    final aboveTaskbar = s['TaskbarMode'] == 'Leave' && s['ReserveWorkArea'] != true;
    final area = aboveTaskbar ? mon.workRect : mon.rect;
    final plate = d.model.axis.rect(left, m.plateTop, right, m.plateBottom);
    int x, y;
    if (!vertical) {
      x = along.round();
      y = (area.bottom - ((bottomOffset + m.plateBottom) * scale).round()).round();
    } else {
      y = along.round();
      x = edge == 'Left' ? (area.left + ((bottomOffset - plate.left) * scale).round()).round() : (area.right - ((bottomOffset + plate.right) * scale).round()).round();
    }
    d.screenPlate = Rect.fromLTWH(x + plate.left * scale, y + plate.top * scale, plate.width * scale, plate.height * scale);
    await d.handle.setBounds(x: x, y: y, width: size.width, height: size.height);
    if (!d.shown && !d.model.isHidden && !_userHidden && !d.concealed) await _showDock(d);
    _scheduleAnchors();
  }

  Future<void> _showDock(_Dock d) async {
    d.shown = true;
    await d.handle.show(activate: false);
    d.model.requestFrame();
  }

  Future<void> _reserve() async {
    final s = state.settings;
    final reserve = (s['ReserveWorkArea'] == true || s['KeepDockInFullscreen'] == true) && s['TaskbarMode'] != 'Leave' && s['AutoHide'] != true && !_userHidden;
    final edge = switch ('${s['Edge'] ?? 'Bottom'}') { 'Left' => 0, 'Right' => 2, _ => 3 };
    for (final d in _docks.values) {
      if (reserve && d.shown) {
        final size = (((s['BottomOffset'] as num?) ?? 10) + ((s['DockHeight'] as num?) ?? 64) + 6) * d.monitor.scale;
        await native.appBar(d.handle.viewId, edge: edge, size: size.ceil(), monitor: d.monitor);
        d.appBarOn = true;
      } else if (d.appBarOn) {
        await native.removeAppBar(d.handle.viewId);
        d.appBarOn = false;
      }
    }
  }

  // ------------------------------------------------------------------ visibility

  void _applySpace(Map<String, Object?> data) {
    final list = ((data['monitors'] as List?) ?? const []).cast<Map>();
    for (final d in _docks.values) {
      final entry = list.where((m) => m['x'] == d.monitor.x && m['y'] == d.monitor.y).firstOrNull;
      d.concealed = entry?['hide'] == true;
      d.blockReveal = entry?['blockReveal'] == true;
      d.model.concealed = d.concealed;
      if (d.concealed) d.model.endHover();
      _wake(d);
    }
  }

  void _wake(_Dock d) => d.model.requestFrame();

  void _onModel(_Dock d) {
    // Fully slid away (auto-hide, a fullscreen window): take the window off the screen so it cannot block what is under it.
    if (d.model.isHidden && d.shown) {
      d.shown = false;
      d.handle.hide();
    }
  }

  Future<void> _edge() async {
    final hidden = _docks.values.where((d) => !d.shown && !d.blockReveal && !d.model.suspended).toList();
    if (hidden.isEmpty || state.setting<bool>('RevealOnEdgeHover', true) != true) return;
    if (_userHidden) return;
    final cursor = await native.cursor();
    final edge = '${state.settings['Edge'] ?? 'Bottom'}';
    for (final d in hidden) {
      final mon = d.monitor;
      final plate = d.screenPlate;
      if (plate == null) continue;
      final onEdge = switch (edge) {
        'Left' => cursor.dx <= mon.x + 1 && cursor.dy >= plate.top && cursor.dy <= plate.bottom,
        'Right' => cursor.dx >= mon.x + mon.w - 2 && cursor.dy >= plate.top && cursor.dy <= plate.bottom,
        _ => cursor.dy >= mon.y + mon.h - 2 && cursor.dx >= plate.left - 20 && cursor.dx <= plate.right + 20,
      };
      if (onEdge) {
        d.model
          ..hide = math.max(d.model.hide, .999)
          ..idleFor = 0;
        await _showDock(d);
      }
    }
  }

  // ------------------------------------------------------------------ actions

  void _activate(_Dock d, DockEntry e, bool newInstance) {
    app.closePreview();
    if (e.kind == DockEntryKind.launchpad) {
      unawaited(app.toggleLaunchpad());
      return;
    }
    if (e.kind == DockEntryKind.app && newInstance && e.app != null) {
      state.call('apps.launch', {'app': e.app, 'newInstance': true}).catchError((_) => null);
      return;
    }
    state.call('dock.activate', {'key': e.key, 'newInstance': newInstance}).catchError((_) => null);
  }

  Future<void> focusDock() async {
    if (_userHidden) {
      await state.call('dock.setHidden', {'hidden': false});
      return;
    }
    final mon = await app.monitorUnderCursor();
    final d = _docks.values.where((d) => d.monitor.x == mon.x && d.monitor.y == mon.y).firstOrNull ?? _docks.values.firstOrNull;
    if (d == null) return;
    if (!d.shown) await _showDock(d);
    await native.setNoActivate(d.handle.viewId, false);
    await native.focus(d.handle.viewId);
    d.model.enterKeyboardMode();
  }

  void _onPopup() {
    final open = app.popupVisible.value && app.openPopupKind == 'context';
    for (final d in _docks.values) {
      if (d.model.menuOpen != open) {
        d.model.menuOpen = open;
        if (!open) d.model.idleFor = 0;
        d.model.requestFrame();
      }
    }
  }

  Future<void> _contextMenu(_Dock d, DockEntry? e, Offset windowPoint) async {
    app.closePreview();
    final info = e == null ? null : await state.call('dock.menuInfo', {'key': e.key}) as Map?;
    final items = <ContextItem>[];
    final appRef = e?.app;
    Future<void> call(String m, [Map<String, Object?>? p]) async {
      try {
        await state.call(m, p);
      } catch (_) {}
    }

    if (e != null && e.kind == DockEntryKind.app && appRef != null) {
      final running = e.running;
      items.addAll([
        ContextItem(running ? 'New window' : 'Open', icon: Icons.open_in_new_rounded, onTap: () => call('apps.launch', {'app': appRef, 'newInstance': running})),
        ContextItem(e.pinned ? 'Unpin from Dock' : 'Pin to Dock', icon: e.pinned ? Icons.push_pin_outlined : Icons.push_pin_rounded, onTap: () => call(e.pinned ? 'pinned.remove' : 'pinned.addApp', e.pinned ? {'key': e.key} : {'app': appRef})),
        if (running)
          ContextItem(e.windows.length > 1 ? trf('Close all windows ({0})', [e.windows.length]) : 'Close window', icon: Icons.close_rounded, translate: e.windows.length <= 1, onTap: () => call('dock.close', {'key': e.key})),
        ContextItem('Open file location', icon: Icons.folder_open_rounded, enabled: info?['hasLocation'] == true, onTap: () => call('apps.reveal', {'app': appRef})),
        const ContextItem.separator(),
      ]);
    } else if (e != null && e.kind == DockEntryKind.launchpad) {
      items.addAll([ContextItem('Open Launchpad', icon: Icons.apps_rounded, onTap: () => app.toggleLaunchpad()), const ContextItem.separator()]);
    } else if (e != null && e.kind == DockEntryKind.separator && e.pinned) {
      final index = (info?['pinnedIndex'] as num?)?.toInt() ?? -1, count = (info?['pinnedCount'] as num?)?.toInt() ?? 0;
      items.addAll([
        ContextItem('Remove separator', icon: Icons.delete_outline_rounded, onTap: () => call('pinned.remove', {'key': e.key})),
        ContextItem('Move separator left', icon: Icons.arrow_back_rounded, enabled: index > 0, onTap: () => call('pinned.reorder', {'key': e.key, 'index': index - 1})),
        ContextItem('Move separator right', icon: Icons.arrow_forward_rounded, enabled: index >= 0 && index < count - 1, onTap: () => call('pinned.reorder', {'key': e.key, 'index': index + 1})),
        const ContextItem.separator(),
      ]);
    }
    final special = e?.kind == DockEntryKind.app || e?.kind == DockEntryKind.start || e?.kind == DockEntryKind.launchpad;
    if (special && appRef != null) {
      items.addAll([
        ContextItem('Change icon…', icon: Icons.image_outlined, onTap: () async {
          final path = await native.pickFile(d.handle.viewId, filters: const [('Images', '*.png;*.ico')]);
          if (path != null) await call('icons.override', {'key': appRef['Key'], 'path': path});
        }),
        ContextItem('Reset icon', icon: Icons.restart_alt_rounded, enabled: info?['hasOverride'] == true || e?.kind != DockEntryKind.app, onTap: () => call('icons.reset', {'key': appRef['Key']})),
        const ContextItem.separator(),
      ]);
    }
    items.addAll([
      ContextItem(e?.pinned == true ? 'Add separator after this item' : 'Add separator', icon: Icons.add_rounded, onTap: () => call('pinned.addSeparator', {'afterKey': ?(e?.pinned == true ? e?.key : null)})),
      ContextItem('Launchpad', icon: Icons.apps_rounded, onTap: () => app.toggleLaunchpad()),
      ContextItem('Dock settings…', icon: Icons.settings_rounded, onTap: () => app.openSettings('Dock')),
      ContextItem('Quit FloatingDock', icon: Icons.logout_rounded, onTap: () => call('app.quit')),
    ]);

    final box = await d.handle.bounds();
    final scale = ((box['dpi'] as num?) ?? 96) / 96;
    final origin = Offset((box['x'] as num).toDouble(), (box['y'] as num).toDouble());
    final screen = origin + windowPoint * scale;
    final edge = '${state.settings['Edge'] ?? 'Bottom'}';
    final plate = d.screenPlate ?? Rect.fromLTWH(screen.dx, screen.dy, 0, 0);
    switch (edge) {
      case 'Left':
        await app.showContextMenu(items, Offset(plate.right + 8 * scale, screen.dy));
      case 'Right':
        await app.showContextMenu(items, Offset(plate.left - 8 * scale - contextMenuWidth * scale, screen.dy));
      default:
        await app.showContextMenu(items, Offset(screen.dx, math.min(screen.dy, plate.top - 8 * scale)), above: true);
    }
  }

  // ------------------------------------------------------------------ window previews

  final _preview = PreviewModel();
  WindowHandle? _previewWin;
  Future<WindowHandle>? _previewCreating;
  Timer? _previewHideTimer;
  int _previewGeneration = 0;

  Future<void> _showPreview(_Dock d, DockEntry e, Rect iconRect) async {
    if (d.model.menuOpen || app.openPopupKind != null || e.windows.isEmpty) return;
    final generation = ++_previewGeneration;
    _previewHideTimer?.cancel();
    final windows = e.windows.take(6).toList();
    final handle = await (_previewCreating ??= app.host.create(
      WindowSpec(kind: 'preview', x: d.monitor.x, y: d.monitor.y, width: 300, height: 200, topmost: true, noActivate: true, title: 'FloatingDock preview'),
      (context) => PreviewView(model: _preview),
    ));
    if (_previewWin == null) {
      _previewWin = handle;
      _preview
        ..onPointer = ((inside) {
          d.model.previewInside = inside;
          if (inside) {
            _previewHideTimer?.cancel();
          } else {
            _scheduleHidePreview();
          }
        })
        ..onActivate = ((w) {
          closePreview();
          state.call('windows.activate', {'hwnd': w.hwnd}).catchError((_) => null);
        })
        ..onClose = ((w) => state.call('windows.close', {'hwnd': w.hwnd}).catchError((_) => null))
        ..onThumbnails = ((rects) {
          native.thumbnails(handle.viewId, [
            for (final (w, r) in rects) {'hwnd': w.hwnd, 'x': r.left, 'y': r.top, 'w': r.width, 'h': r.height, 'opacity': 1.0},
          ]).catchError((_) => null);
        });
    }
    // Pixel sizes of the windows give the pictures their aspect ratio.
    final cards = <PreviewLayout>[];
    for (final w in windows) {
      final sz = await native.windowSize(handle.viewId, w.hwnd);
      cards.add(PreviewLayout(w, thumbSizeFor(sz.isEmpty ? null : Size((sz['w'] as num).toDouble(), (sz['h'] as num).toDouble()))));
    }
    if (generation != _previewGeneration) return;
    final thumbH = cards.map((c) => c.thumbSize.height).reduce(math.max);
    final panel = PreviewModel.panelSize(cards.length, thumbH);
    final mon = d.monitor;
    final scale = mon.scale;
    final box = await d.handle.bounds();
    final origin = Offset((box['x'] as num).toDouble(), (box['y'] as num).toDouble());
    final edge = '${state.settings['Edge'] ?? 'Bottom'}';
    final iconScreen = Rect.fromLTWH(origin.dx + iconRect.left * scale, origin.dy + iconRect.top * scale, iconRect.width * scale, iconRect.height * scale);
    final pad = (28 * scale).round();
    final wPx = (panel.width * scale).round(), hPx = (panel.height * scale).round();
    int x, y;
    final plate = d.screenPlate ?? iconScreen;
    switch (edge) {
      case 'Left':
        x = (plate.right + 8 * scale).round();
        y = (iconScreen.center.dy - hPx / 2).round();
      case 'Right':
        x = (plate.left - 8 * scale - wPx).round();
        y = (iconScreen.center.dy - hPx / 2).round();
      default:
        x = (iconScreen.center.dx - wPx / 2).round();
        y = (plate.top - 8 * scale - hPx).round();
    }
    x = x.clamp(mon.x + 4, math.max(mon.x + 4, mon.x + mon.w - wPx - 4)).toInt();
    y = y.clamp(mon.y + 4, math.max(mon.y + 4, mon.y + mon.h - hPx - 4)).toInt();
    await handle.setBounds(x: x - pad, y: y - pad, width: panel.width + 56, height: panel.height + 56);
    _preview.open(appName: e.name, app: e.app, cards: cards);
    _preview.visible.value = true;
    await handle.show(activate: false);
  }

  void _scheduleHidePreview() {
    _previewHideTimer?.cancel();
    if (_previewWin == null || !_preview.visible.value) return;
    _previewHideTimer = Timer(const Duration(milliseconds: 220), () {
      if (_docks.values.any((d) => d.model.previewInside)) return;
      closePreview();
    });
  }

  void closePreview() {
    _previewGeneration++;
    _previewHideTimer?.cancel();
    final handle = _previewWin;
    if (handle == null || !_preview.visible.value) return;
    native.thumbnails(handle.viewId, const []).catchError((_) => null);
    _preview.visible.value = false;
    Future<void>.delayed(const Duration(milliseconds: 190), () {
      if (!_preview.visible.value) handle.hide();
    });
  }

  // ------------------------------------------------------------------ shell animation targets

  void _scheduleAnchors() {
    _anchorTimer?.cancel();
    _anchorTimer = Timer(const Duration(milliseconds: 250), _publishAnchors);
  }

  Future<void> _publishAnchors() async {
    final anchors = <Map<String, Object?>>[];
    Map<String, Object?>? fallback;
    for (final d in _docks.values) {
      final plate = d.screenPlate;
      if (plate == null || !d.shown) continue;
      final box = await d.handle.bounds();
      final scale = ((box['dpi'] as num?) ?? 96) / 96;
      final origin = Offset((box['x'] as num).toDouble(), (box['y'] as num).toDouble());
      for (final it in d.model.motion.items) {
        if (it.isSeparator || it.leaving || it.entry.kind != DockEntryKind.app) continue;
        final r = d.model.axis.rect(it.baseX, it.bottom - d.model.gp.iconSize, it.baseX + d.model.gp.iconSize, it.bottom);
        anchors.add({
          'key': it.key,
          'left': (origin.dx + r.left * scale).round(), 'top': (origin.dy + r.top * scale).round(),
          'right': (origin.dx + r.right * scale).round(), 'bottom': (origin.dy + r.bottom * scale).round(),
        });
      }
      final size = d.model.gp.iconSize * scale;
      fallback ??= {
        'left': (plate.center.dx - size / 2).round(), 'top': (plate.center.dy - size / 2).round(),
        'right': (plate.center.dx + size / 2).round(), 'bottom': (plate.center.dy + size / 2).round(),
      };
    }
    try {
      await state.call('dock.anchors', {'anchors': anchors, 'fallback': ?fallback});
    } catch (_) {}
  }
}
