import 'dart:async';
import 'dart:math' as math;

import 'package:fd_ipc/client.dart';
import 'package:flutter/widgets.dart';

import '../core/app_state.dart';
import '../core/sfx.dart';
import '../core/native.dart';
import '../windows/launchpad/launchpad_window.dart';
import '../windows/popup/context_menu.dart';
import '../windows/popup/menus.dart';
import '../windows/popup/popup_view.dart';
import '../windows/search/search_window.dart';
import '../windows/settings/settings_window.dart';
import 'bar_controller.dart';
import 'dock_controller.dart';
import 'host.dart';

/// Owns the windows of the UI process and reacts to what the engine says: hotkeys, tray commands, display changes.
/// Each kind of window is created lazily the first time it is needed.
class AppController {
  AppController({required this.state, required this.host});

  final AppState state;
  final WindowHost host;
  StreamSubscription<FdEvent>? _events;
  StreamSubscription<NativeEvent>? _nativeEvents;
  Timer? _lostEngine;
  final _exit = Completer<void>();

  /// Completes when the process should end (the engine is quitting or gone).
  Future<void> get exitRequested => _exit.future;

  late final BarController _bars = BarController(this);
  late final DockController _dock = DockController(this);

  void start() {
    state.actions['search'] = ([_]) => toggleSearch();
    state.actions['launchpad'] = ([_]) => toggleLaunchpad();
    state.actions['settings'] = ([section]) => openSettings(section);
    state.actions['menu'] = ([kind]) => openMenuAt(kind ?? 'main');
    _events = state.eventsOther.listen(_onEvent);
    _nativeEvents = native.events.listen(_onNative);
    state.addListener(_onState);
    _onState();
    _bars.start();
    _dock.start();
  }

  void _onEvent(FdEvent e) {
    switch (e.name) {
      case 'ui.command':
        final data = (e.data as Map).cast<String, Object?>();
        _command('${data['command']}', data);
      case 'engine.stopping':
        if (!_exit.isCompleted) _exit.complete();
    }
  }

  void _command(String name, Map<String, Object?> data) {
    switch (name) {
      case 'settings':
        unawaited(openSettings(data['section'] as String?));
      case 'toggleSearch':
        unawaited(toggleSearch());
      case 'toggleLaunchpad':
        unawaited(toggleLaunchpad());
      case 'menu':
        unawaited(openMenuAt('${data['kind'] ?? 'main'}'));
    }
  }

  bool _wasConnected = true;
  bool _underOverlay = false;

  void _onState() {
    if (state.captureHidden != _underOverlay) {
      _underOverlay = state.captureHidden;
      WindowHost.underOverlay = _underOverlay;
    }
    // The engine owns the system; without it this process has nothing to show. Give a restarting engine time to come back.
    if (state.connected && !_wasConnected) _lostEngine?.cancel();
    if (!state.connected && _wasConnected) {
      _lostEngine?.cancel();
      _lostEngine = Timer(const Duration(seconds: 20), () {
        if (!state.connected && !_exit.isCompleted) _exit.complete();
      });
    }
    _wasConnected = state.connected;
    _syncSettingsWindow();
  }

  // ------------------------------------------------------------------ settings window

  WindowHandle? _settings;
  Future<WindowHandle>? _settingsCreating;
  final _settingsPage = ValueNotifier<String?>(null);
  String _settingsLanguage = '';
  bool _settingsDark = true;

  Future<MonitorInfo> primaryMonitor() async {
    final monitors = await native.monitors();
    return monitors.firstWhere((m) => m.primary, orElse: () => monitors.first);
  }

  Future<void> openSettings([String? section]) async {
    final WindowHandle handle;
    try {
      handle = await (_settingsCreating ??= _createSettings());
    } catch (_) {
      _settingsCreating = null;     // a failed attempt must not make every later one fail with it
      rethrow;
    }
    if (section != null) {
      _settingsPage.value = null;
      _settingsPage.value = section;
    }
    await handle.show();
    Sfx.play(Sound.open);
    _syncSettingsWindow();
  }

  Future<WindowHandle> _createSettings() async {
    final m = await primaryMonitor();
    const w = 1040.0, h = 760.0;
    final pw = (w * m.scale).round(), ph = (h * m.scale).round();
    final x = m.workX + math.max<int>(0, (m.workW - pw) ~/ 2);
    final y = m.workY + math.max<int>(0, (m.workH - ph) ~/ 2);
    final handle = await host.create(
      WindowSpec(
        kind: 'settings',
        x: x,
        y: y,
        width: w,
        height: h,
        framed: true,
        minWidth: 760,
        minHeight: 520,
        title: tr('FloatingDock settings'),
        darkCaption: state.brightness == Brightness.dark,
        toolWindow: false,
      ),
      (context) => SettingsView(navigate: _settingsPage),
    );
    _settings = handle;
    native.events.listen((e) {
      // The close button hides the window; the dock keeps running.
      if (e.viewId == handle.viewId && e.type == 'closeRequested') handle.hide();
    });
    return handle;
  }

  void _syncSettingsWindow() {
    final handle = _settings;
    if (handle == null) return;
    final dark = state.brightness == Brightness.dark;
    if (dark != _settingsDark) {
      _settingsDark = dark;
      native.setDarkCaption(handle.viewId, dark);
    }
    if (state.language != _settingsLanguage) {
      _settingsLanguage = state.language;
      native.setTitle(handle.viewId, tr('FloatingDock settings'));
    }
  }

  // ------------------------------------------------------------------ shared helpers

  void _onNative(NativeEvent e) {
    final menu = _popupWin;
    if (menu != null && e.viewId == menu.viewId && e.type == 'activated' && e.data['active'] == false) {
      if (_popup.visible.value && DateTime.now().difference(_popupShownAt) > const Duration(milliseconds: 300)) closePopup();
    }
    final pad = _launchpadWin;
    if (pad != null && e.viewId == pad.viewId) {
      if (e.type == 'filesDropped') _launchpad.dropped.add((e.data['paths'] as List).cast<String>());
      if (e.type == 'activated' && e.data['active'] == false) _launchpadDeactivated();
    }
    final search = _searchWin;
    if (search != null && e.viewId == search.viewId && e.type == 'activated' && e.data['active'] == false) {
      // Losing focus closes the palette (not in the first moments, while the window is still taking focus).
      if (_search.visible.value && DateTime.now().difference(_searchShownAt) > const Duration(milliseconds: 300)) hideSearch();
    }
  }

  Future<MonitorInfo> monitorUnderCursor() async {
    final monitors = await native.monitors();
    final c = await native.cursor();
    return monitors.firstWhere((m) => m.rect.contains(c), orElse: () => monitors.firstWhere((m) => m.primary, orElse: () => monitors.first));
  }

  /// Hides [handle] after its surface has played the leaving animation, unless it was shown again meanwhile.
  void _hideLater(WindowHandle handle, ValueNotifier<bool> shown, {int ms = 190}) {
    shown.value = false;
    Future<void>.delayed(Duration(milliseconds: ms), () {
      if (!shown.value) handle.hide();
    });
  }

  // ------------------------------------------------------------------ search

  final _search = SearchModel();
  WindowHandle? _searchWin;
  Future<WindowHandle>? _searchCreating;
  DateTime _searchShownAt = DateTime.fromMillisecondsSinceEpoch(0);

  /// The search opened out of the dock and has taken it off the screen: the dock returns with it.
  bool _searchHidesDock = false;

  Future<void> toggleSearch() => _search.visible.value ? hideSearch() : showSearch();

  Future<void> showSearch() async {
    if (_search.visible.value) return;
    final mon = await monitorUnderCursor();
    final scale = mon.scale;
    final panelW = math.min(650.0, mon.w / scale - 40);
    var maxH = math.min(580.0, mon.h / scale - 60);
    final pad = (28 * scale).round();
    // Dropping in from the top: the window is the panel and a margin around it.
    var x = mon.x + ((mon.w - panelW * scale) / 2).round() - pad;
    var y = mon.y + (mon.h * .18).round() - pad;
    y = math.min(y, mon.y + mon.h - (maxH * scale).round() - 16 - pad);
    var winW = panelW + 56, winH = maxH + 56;
    var origin = const Offset(28, 28);
    Offset? from;
    final plate = _dock.plateOn(mon);
    final edge = '${state.settings['Edge'] ?? 'Bottom'}';
    String? dockEdge;
    if (plate != null) {
      // The dock goes away and the search takes its place: a panel flush with the dock's screen edge, in front of where the plate stood,
      // growing away from the edge; it starts as a small card at the plate. Its window ends exactly at the screen's edge.
      dockEdge = edge == 'Left' || edge == 'Right' ? edge : 'Bottom';
      final off = (((state.settings['BottomOffset'] as num?) ?? 10) * scale).toDouble();
      final margin = 28 * scale, fillet = SearchModel.fillet * scale;
      final pw = panelW * scale;
      if (dockEdge == 'Bottom') {
        final flush = plate.bottom + off;
        maxH = math.min(580.0, (flush - mon.y) / scale - 24);
        final ph = maxH * scale, shapeW = pw + 2 * fillet;
        final left = (plate.center.dx - shapeW / 2)
            .clamp(mon.x + 8 * scale, math.max(mon.x + 8 * scale, mon.x + mon.w - shapeW - 8 * scale))
            .toDouble();
        final win = Rect.fromLTRB(left - margin, flush - ph - margin, left + shapeW + margin, flush);
        x = win.left.round();
        y = win.top.round();
        winW = win.width / scale;
        winH = win.height / scale;
        from = Offset((plate.center.dx - (left + shapeW / 2)) / scale, (plate.center.dy - flush) / scale);
      } else {
        maxH = math.min(580.0, mon.h / scale - 2 * SearchModel.fillet - 56 - 24);
        final ph = maxH * scale, longSide = ph + 2 * fillet + 2 * margin;
        final flush = dockEdge == 'Left' ? plate.left - off : plate.right + off;
        final top = (plate.center.dy - longSide / 2).clamp(mon.y.toDouble(), math.max(mon.y.toDouble(), mon.y + mon.h - longSide)).toDouble();
        final left = dockEdge == 'Left' ? flush : flush - pw - margin;
        x = left.round();
        y = top.round();
        winW = (pw + margin) / scale;
        winH = longSide / scale;
        from = Offset((plate.center.dx - flush) / scale, (plate.center.dy - (top + longSide / 2)) / scale);
      }
    }
    final handle = await (_searchCreating ??= host.create(
      WindowSpec(kind: 'search', x: x, y: y, width: winW, height: winH, topmost: true, title: 'FloatingDock Search'),
      (context) => SearchView(model: _search),
    ));
    if (_searchWin == null) {
      _searchWin = handle;
      _search.onClose = hideSearch;
    }
    await handle.setBounds(x: x, y: y, width: winW, height: winH);
    _search.reopen(width: panelW, height: maxH, panelOrigin: origin, from: from, dockEdge: dockEdge);
    _searchShownAt = DateTime.now();
    _search.visible.value = true;
    Sfx.play(Sound.open);
    if (plate != null) {
      _searchHidesDock = true;
      state.actions['dockSuspended']?.call('true');
    }
    await handle.show();
  }

  Future<void> hideSearch() async {
    final handle = _searchWin;
    if (handle == null || !_search.visible.value) return;
    Sfx.play(Sound.close);
    _hideLater(handle, _search.visible, ms: _search.from == null ? 190 : 300);
    if (_searchHidesDock) {
      _searchHidesDock = false;
      state.actions['dockSuspended']?.call('false');
    }
  }

  // ------------------------------------------------------------------ popup menus (bar menus, context menus)

  final _popup = PopupModel();
  WindowHandle? _popupWin;
  Future<WindowHandle>? _popupCreating;
  DateTime _popupShownAt = DateTime.fromMillisecondsSinceEpoch(0);

  String? get openPopupKind => _popup.visible.value ? _popup.kind : null;

  void closePreview() => _dock.closePreview();
  ValueNotifier<bool> get popupVisible => _popup.visible;

  /// Opens (or, for the same kind, closes) a menu anchored under [anchor], a rectangle in physical screen pixels.
  /// [alignLeft] puts the panel's left edge at the anchor's (the main menu); otherwise its right edge.
  Future<void> openPopup(String kind, Rect anchor, {WidgetBuilder? builder, double? width, bool alignLeft = false, bool toggle = true}) async {
    if (toggle && _popup.visible.value && _popup.kind == kind) return closePopup();
    final monitors = await native.monitors();
    final mon = monitors.firstWhere(
      (m) => m.rect.contains(anchor.center),
      orElse: () => monitors.firstWhere((m) => m.primary, orElse: () => monitors.first),
    );
    final panelW = width ?? menuWidth(kind);
    final panelPx = (panelW * mon.scale).round();
    final pad = (28 * mon.scale).round();
    var x = alignLeft ? anchor.left.round() : anchor.right.round() - panelPx;
    x = x.clamp(mon.x + 8, math.max<int>(mon.x + 8, mon.x + mon.w - panelPx - 8)).toInt();
    final y = anchor.bottom.round() + (6 * mon.scale).round();
    final maxH = math.max(160.0, (mon.y + mon.h - y) / mon.scale - 16);
    final pivot = ((anchor.center.dx - x) / panelPx);
    await _showPopup(kind, x - pad, y - pad, panelW, maxH, pivot, builder ?? (_) => menuFor(kind), mon);
  }

  /// A context menu at a point (the pointer); [above] makes it grow upwards from that point (dock menus).
  Future<void> showContextMenu(List<ContextItem> items, Offset screenPx, {bool above = false}) async {
    final monitors = await native.monitors();
    final mon = monitors.firstWhere(
      (m) => m.rect.contains(screenPx),
      orElse: () => monitors.firstWhere((m) => m.primary, orElse: () => monitors.first),
    );
    final panelPx = (contextMenuWidth * mon.scale).round();
    final heightDip = contextMenuHeight(items);
    final heightPx = (heightDip * mon.scale).round();
    final pad = (28 * mon.scale).round();
    final x = screenPx.dx.round().clamp(mon.x + 8, math.max<int>(mon.x + 8, mon.x + mon.w - panelPx - 8)).toInt();
    var y = above ? screenPx.dy.round() - heightPx : screenPx.dy.round();
    y = y.clamp(mon.y + 8, math.max<int>(mon.y + 8, mon.y + mon.h - heightPx - 8)).toInt();
    await _showPopup(
      'context',
      x - pad,
      y - pad,
      contextMenuWidth,
      heightDip,
      (screenPx.dx - x) / panelPx,
      (_) => ContextMenuContent(items: items),
      mon,
    );
  }

  Future<void> _showPopup(String kind, int winX, int winY, double panelW, double maxH, double pivot, WidgetBuilder builder, MonitorInfo mon) async {
    final handle = await (_popupCreating ??= host.create(
      WindowSpec(kind: 'menu', x: winX, y: winY, width: panelW + 56, height: maxH + 56, topmost: true, title: 'FloatingDock menu'),
      (context) => PopupView(model: _popup),
    ));
    if (_popupWin == null) {
      _popupWin = handle;
      _popup.onClose = closePopup;
    }
    await handle.setBounds(x: winX, y: winY, width: panelW + 56, height: maxH + 56);
    _popup.open(kind: kind, builder: builder, panelWidth: panelW, maxHeight: maxH, pivot: pivot);
    _popupShownAt = DateTime.now();
    _popupRectPx = Rect.fromLTWH(winX.toDouble(), winY.toDouble(), (panelW + 56) * mon.scale, (maxH + 56) * mon.scale);
    _popup.visible.value = true;
    await handle.show();
    Sfx.play(Sound.open);
    _watchOutsideClicks();
  }

  Rect _popupRectPx = Rect.zero;
  Timer? _outsideTimer;

  /// A menu closes when the mouse is released anywhere outside its window (other windows never take the focus from us,
  /// so there is no deactivation to wait for). The pointer's state is read while a menu is open only.
  void _watchOutsideClicks() {
    _outsideTimer?.cancel();
    var wasDown = false;
    var busy = false;
    _outsideTimer = Timer.periodic(const Duration(milliseconds: 30), (timer) async {
      if (!_popup.visible.value) return timer.cancel();
      if (busy) return;
      busy = true;
      try {
        final s = await native.mouseState();
        final released = wasDown && !s.down;
        wasDown = s.down;
        if (!released || _popupRectPx.contains(s.pos)) return;
        if (DateTime.now().difference(_popupShownAt) < const Duration(milliseconds: 350)) return;
        // Give a bar/dock button that was clicked time to handle its own toggle first.
        final shown = _popupShownAt;
        await Future<void>.delayed(const Duration(milliseconds: 160));
        if (_popup.visible.value && _popupShownAt == shown) await closePopup();
      } catch (_) {
      } finally {
        busy = false;
      }
    });
  }

  Future<void> closePopup() async {
    final handle = _popupWin;
    if (handle == null || !_popup.visible.value) return;
    _hideLater(handle, _popup.visible);
    // A menu opened from the Launchpad hands the focus back to it.
    final pad = _launchpadWin;
    if (pad != null && _launchpad.visible.value) native.focus(pad.viewId);
  }

  // ------------------------------------------------------------------ Launchpad

  final _launchpad = LaunchpadModel();
  WindowHandle? _launchpadWin;
  Future<WindowHandle>? _launchpadCreating;
  DateTime _launchpadShownAt = DateTime.fromMillisecondsSinceEpoch(0);
  int _launchpadModal = 0;

  Future<void> toggleLaunchpad() => _launchpad.visible.value ? hideLaunchpad() : showLaunchpad();

  Future<void> showLaunchpad() async {
    if (_launchpad.visible.value) return;
    final mon = await monitorUnderCursor();
    final w = mon.w / mon.scale, h = mon.h / mon.scale;
    final handle = await (_launchpadCreating ??= host.create(
      WindowSpec(kind: 'launchpad', x: mon.x, y: mon.y, width: w, height: h, topmost: true, acceptFiles: true, title: 'FloatingDock Launchpad'),
      (context) => LaunchpadView(model: _launchpad),
    ));
    if (_launchpadWin == null) {
      _launchpadWin = handle;
      _launchpad.onClose = hideLaunchpad;
      _launchpad.onContextMenu = (items, at) async {
        final screen = await _viewToScreen(handle, at);
        await showContextMenu(items, screen);
      };
      _launchpad.pickFile = (filters) async {
        _launchpadModal++;
        try {
          return await native.pickFile(handle.viewId, filters: filters);
        } finally {
          _launchpadModal--;
          if (_launchpad.visible.value) native.focus(handle.viewId);
        }
      };
    }
    await handle.setBounds(x: mon.x, y: mon.y, width: w, height: h);
    _launchpad.reopen(Size(w, h));
    _launchpadShownAt = DateTime.now();
    _launchpad.visible.value = true;
    await handle.show();
    Sfx.play(Sound.open);
    state.actions['dockSuspended']?.call('true');
  }

  Future<void> hideLaunchpad() async {
    final handle = _launchpadWin;
    if (handle == null || !_launchpad.visible.value) return;
    Sfx.play(Sound.close);
    _hideLater(handle, _launchpad.visible);
    state.actions['dockSuspended']?.call('false');
  }

  void _launchpadDeactivated() {
    if (!_launchpad.visible.value || _launchpadModal > 0 || _popup.visible.value) return;
    if (DateTime.now().difference(_launchpadShownAt) < const Duration(milliseconds: 400)) return;
    // Focus often moves for a moment (a menu, a dialog); only a stable change dismisses.
    Future<void>.delayed(const Duration(milliseconds: 200), () async {
      final handle = _launchpadWin;
      if (handle == null || !_launchpad.visible.value || _launchpadModal > 0 || _popup.visible.value) return;
      final b = await handle.bounds();
      if (b['active'] != true) hideLaunchpad();
    });
  }

  /// A point in a view (logical pixels) as a screen position (physical pixels).
  Future<Offset> _viewToScreen(WindowHandle handle, Offset local) async {
    final b = await handle.bounds();
    final scale = ((b['dpi'] as num?) ?? 96) / 96;
    return Offset((b['x'] as num).toDouble() + local.dx * scale, (b['y'] as num).toDouble() + local.dy * scale);
  }

  /// Opens one of the bar menus near the right end of the primary monitor's top edge, as if its button had been clicked.
  /// Used by the keyboard and by tests: the real buttons pass their own rectangle.
  Future<void> openMenuAt(String kind) async {
    final mon = await primaryMonitor();
    final anchor = Rect.fromLTWH((mon.x + mon.w - 260 * mon.scale), mon.y + 4 * mon.scale, 40 * mon.scale, 36 * mon.scale);
    await openPopup(kind, anchor, alignLeft: kind == 'main');
  }

  void dispose() {
    _bars.dispose();
    _dock.dispose();
    _events?.cancel();
    _nativeEvents?.cancel();
    _lostEngine?.cancel();
    state.removeListener(_onState);
  }
}
