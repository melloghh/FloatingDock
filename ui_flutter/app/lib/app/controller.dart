import 'dart:async';
import 'dart:math' as math;

import 'package:fd_ipc/client.dart';
import 'package:flutter/foundation.dart';

import '../core/app_state.dart';
import '../core/native.dart';
import '../windows/settings/settings_window.dart';
import 'host.dart';

/// Owns the windows of the UI process and reacts to what the engine says: hotkeys, tray commands, display changes.
/// Each kind of window is created lazily the first time it is needed.
class AppController {
  AppController({required this.state, required this.host});

  final AppState state;
  final WindowHost host;
  StreamSubscription<FdEvent>? _events;
  Timer? _lostEngine;
  final _exit = Completer<void>();

  /// Completes when the process should end (the engine is quitting or gone).
  Future<void> get exitRequested => _exit.future;

  void start() {
    _events = state.eventsOther.listen(_onEvent);
    state.addListener(_onState);
    _onState();
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
    }
  }

  bool _wasConnected = true;

  void _onState() {
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
    final handle = await (_settingsCreating ??= _createSettings());
    if (section != null) {
      _settingsPage.value = null;
      _settingsPage.value = section;
    }
    await handle.show();
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
        kind: 'settings', x: x, y: y, width: w, height: h, framed: true, minWidth: 760, minHeight: 520,
        title: tr('FloatingDock settings'), darkCaption: state.brightness == Brightness.dark, toolWindow: false,
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

  void dispose() {
    _events?.cancel();
    _lostEngine?.cancel();
    state.removeListener(_onState);
  }
}
