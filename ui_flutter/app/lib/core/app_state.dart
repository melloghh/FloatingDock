import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';
import 'package:flutter/material.dart';

import 'engine_api.dart';
import 'theme.dart';

/// Everything every window shares: the engine's data (settings, pinned apps, windows, texts) and the theme derived from it.
/// One instance per process, so a change made in one window reaches all the others in the same frame.
class AppState extends ChangeNotifier {
  AppState(this.engine, {this.systemBrightness}) {
    _subscription = engine.events.listen(_onEvent);
    _connection = engine.connection.listen((up) {
      if (up) unawaited(refresh());
      notifyListeners();
    });
  }

  final EngineApi engine;
  late final StreamSubscription<FdEvent> _subscription;
  late final StreamSubscription<bool> _connection;
  final Brightness? systemBrightness;

  Map<String, Object?> settings = {};
  List<Map<String, Object?>> pinned = [];
  List<Map<String, Object?>> windows = [];
  int foreground = 0;
  String? foregroundName;
  Map<String, String> texts = {};
  String _textsLanguage = 'English';

  final _pending = <String, Object?>{};
  Timer? _flush;

  bool get connected => engine.connected;

  /// Things only the UI process can do (open a window...), registered by the controller and used by menus.
  /// ct('settings:Launchpad') runs the settings action with the argument Launchpad.
  final actions = <String, FutureOr<void> Function([String? arg])>{};

  void act(String name) {
    final colon = name.indexOf(':');
    final key = colon < 0 ? name : name.substring(0, colon);
    actions[key]?.call(colon < 0 ? null : name.substring(colon + 1));
  }

  /// Calls an engine method (for data that is not kept in this state).
  Future<Object?> call(String method, [Map<String, Object?>? params]) => engine.call(method, params);

  // ------------------------------------------------------------------ loading

  Future<void> refresh() async {
    try {
      final s = await engine.call(Methods.settingsGet);
      if (s is Map) settings = s.cast<String, Object?>();
      final p = await engine.call(Methods.pinnedGet);
      if (p is List) pinned = p.cast<Map>().map((e) => e.cast<String, Object?>()).toList();
      final w = await engine.call(Methods.windowsList);
      if (w is Map) _setWindows(w);
      await _loadTexts();
    } catch (_) {
      // The link dropped mid-refresh: the next connection refreshes again.
    }
    notifyListeners();
  }

  Future<void> _loadTexts() async {
    final t = await engine.call('text.table');
    if (t is Map) {
      _textsLanguage = '${t['language']}';
      texts = ((t['table'] as Map?) ?? {}).cast<String, String>();
      I18n.table = texts;
    }
  }

  void _setWindows(Map w) {
    windows = ((w['windows'] as List?) ?? const []).cast<Map>().map((e) => e.cast<String, Object?>()).toList();
    foreground = (w['foreground'] as num?)?.toInt() ?? 0;
    foregroundName = w['foregroundName'] as String? ?? foregroundName;
  }

  void _onEvent(FdEvent e) {
    switch (e.name) {
      case Events.settingsChanged:
        final incoming = (e.data as Map).cast<String, Object?>();
        // Keys we have just changed locally and not yet sent keep their local value (no jump back while dragging a slider).
        for (final key in _pending.keys) {
          if (incoming.containsKey(key)) incoming[key] = _pending[key];
        }
        final languageChanged = incoming['Language'] != settings['Language'];
        settings = incoming;
        if (languageChanged) unawaited(_loadTexts().then((_) => notifyListeners()));
        notifyListeners();
      case Events.pinnedChanged:
        pinned = (e.data as List).cast<Map>().map((m) => m.cast<String, Object?>()).toList();
        notifyListeners();
      case Events.windowsChanged:
        _setWindows(e.data as Map);
        notifyListeners();
      case 'icons.changed':
        iconGeneration++;
        _icons.clear();
        notifyListeners();
        _otherEvents.add(e);
      default:
        _otherEvents.add(e);
    }
  }

  // ------------------------------------------------------------------ icons

  int iconGeneration = 0;
  final _icons = <String, Future<Uint8List?>>{};

  /// PNG bytes of an app's icon as the engine resolves it (override > icon pack > Windows). Cached until icons change.
  Future<Uint8List?> iconBytes(Map<String, Object?> app, int size, {bool fallback = true}) {
    final key = '${app['Id']}|${app['ExePath']}|${app['Aumid']}|${app['ShortcutPath']}|${app['ShellParsingName']}|$size|$fallback';
    return _icons.putIfAbsent(key, () async {
      try {
        final r = await engine.call('icons.get', {'app': app, 'size': size, 'fallback': fallback});
        final b64 = r is Map ? r['image'] : null;
        return b64 is String ? base64Decode(b64) : null;
      } catch (_) {
        _icons.remove(key);
        return null;
      }
    });
  }

  final _otherEvents = StreamController<FdEvent>.broadcast();

  /// Events other than the three the state itself keeps (hotkeys, tray, media...).
  Stream<FdEvent> get eventsOther => _otherEvents.stream;

  // ------------------------------------------------------------------ settings

  T setting<T>(String key, T fallback) {
    final v = settings[key];
    if (v is T) return v;
    if (T == double && v is num) return v.toDouble() as T;
    if (T == int && v is num) return v.toInt() as T;
    return fallback;
  }

  /// Applies [patch] locally at once and sends it to the engine shortly after (coalesced while a slider is dragged).
  void update(Map<String, Object?> patch) {
    settings = {...settings, ...patch};
    _pending.addAll(patch);
    notifyListeners();
    _flush ??= Timer(const Duration(milliseconds: 60), _send);
  }

  String? lastSettingsError;

  Future<void> _send() async {
    _flush = null;
    final patch = Map<String, Object?>.of(_pending);
    _pending.clear();
    try {
      final s = await engine.call(Methods.settingsSet, {'patch': patch});
      lastSettingsError = null;
      if (s is Map && _pending.isEmpty) settings = s.cast<String, Object?>();
    } on FdRemoteError catch (e) {
      // Rejected as a whole: show the engine's own value again.
      lastSettingsError = e.message;
      final s = await engine.call(Methods.settingsGet);
      if (s is Map) settings = s.cast<String, Object?>();
    } catch (_) {
      lastSettingsError = 'The engine is not reachable.';
    }
    notifyListeners();
  }

  // ------------------------------------------------------------------ theme

  Brightness get brightness {
    switch (setting<String>('Theme', 'Dark')) {
      case 'Light':
        return Brightness.light;
      case 'System':
        return systemBrightness ?? ui.PlatformDispatcher.instance.platformBrightness;
      default:
        return Brightness.dark;
    }
  }

  void systemBrightnessChanged() {
    if (setting<String>('Theme', 'Dark') == 'System') notifyListeners();
  }

  String get seed => setting<String>('AccentSeed', '4E9F75');

  ColorScheme? _scheme;
  String _schemeKey = '';
  ThemeData? _theme;

  ColorScheme get scheme {
    final key = '$seed/${brightness.name}';
    if (_scheme == null || key != _schemeKey) {
      _scheme = schemeFor(seed, brightness);
      _schemeKey = key;
      _theme = null;
    }
    return _scheme!;
  }

  ThemeData get theme => _theme ??= buildTheme(scheme);

  /// The top bar is always a dark shell, whatever the theme (as in the WinUI version).
  ColorScheme get darkScheme => schemeFor(seed, Brightness.dark);

  String get language => _textsLanguage;

  @override
  void dispose() {
    _subscription.cancel();
    _connection.cancel();
    _flush?.cancel();
    super.dispose();
  }
}

/// Product strings: the engine sends the table for the current language (English text -> translation).
abstract final class I18n {
  static Map<String, String> table = {};
}

String tr(String text) => I18n.table[text] ?? text;

/// `trf('Imported {0} app(s).', [3])`.
String trf(String text, List<Object?> args) {
  var s = tr(text);
  for (var i = 0; i < args.length; i++) {
    s = s.replaceAll('{$i}', '${args[i]}');
  }
  return s;
}

/// Gives every widget below access to the shared state; rebuilds dependents when it changes.
class AppScope extends InheritedNotifier<AppState> {
  const AppScope({super.key, required AppState state, required super.child}) : super(notifier: state);

  static AppState of(BuildContext context) {
    final scope = context.dependOnInheritedWidgetOfExactType<AppScope>();
    assert(scope != null, 'No AppScope above this widget.');
    return scope!.notifier!;
  }

  /// Reads the state without subscribing to changes (for callbacks).
  static AppState read(BuildContext context) =>
      (context.getInheritedWidgetOfExactType<AppScope>())!.notifier!;
}
