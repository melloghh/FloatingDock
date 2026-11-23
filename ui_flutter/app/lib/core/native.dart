import 'dart:async';

import 'package:flutter/services.dart';

class MonitorInfo {
  MonitorInfo(Map m)
      : x = m['x'] as int,
        y = m['y'] as int,
        w = m['w'] as int,
        h = m['h'] as int,
        workX = m['wx'] as int,
        workY = m['wy'] as int,
        workW = m['ww'] as int,
        workH = m['wh'] as int,
        dpi = m['dpi'] as int,
        primary = m['primary'] as bool;

  /// Whole monitor and work area, physical pixels in virtual-screen coordinates.
  final int x, y, w, h, workX, workY, workW, workH, dpi;
  final bool primary;
  double get scale => dpi / 96.0;
  Rect get rect => Rect.fromLTWH(x.toDouble(), y.toDouble(), w.toDouble(), h.toDouble());
  Rect get workRect => Rect.fromLTWH(workX.toDouble(), workY.toDouble(), workW.toDouble(), workH.toDouble());
}

class CreatedWindow {
  CreatedWindow(this.viewId, this.hwnd);
  final int viewId;
  final int hwnd;
}

class NativeEvent {
  NativeEvent(this.type, this.viewId, this.data);
  final String type;
  final int viewId;
  final Map data;
}

/// The runner's window API ("fd/windows"). Positions are physical screen pixels, sizes are logical (DIP).
class Native {
  Native([MethodChannel? channel]) : _channel = channel ?? const MethodChannel('fd/windows') {
    _channel.setMethodCallHandler((call) async {
      if (call.method == 'event') {
        final m = call.arguments as Map;
        _events.add(NativeEvent(m['type'] as String, (m['viewId'] as num).toInt(), m));
      }
      return null;
    });
  }

  final MethodChannel _channel;
  final _events = StreamController<NativeEvent>.broadcast();
  Stream<NativeEvent> get events => _events.stream;

  Future<List<MonitorInfo>> monitors() async {
    final r = await _channel.invokeListMethod<Map>('monitors');
    return [for (final m in r ?? const <Map>[]) MonitorInfo(m)];
  }

  /// The pointer position (physical px) and whether any mouse button is down, wherever the pointer is.
  Future<({Offset pos, bool down})> mouseState() async {
    final r = await _channel.invokeMapMethod<String, Object?>('mouseState');
    return (pos: Offset((r!['x'] as int).toDouble(), (r['y'] as int).toDouble()), down: r['down'] == true);
  }

  Future<Offset> cursor() async {
    final r = await _channel.invokeMapMethod<String, Object?>('cursor');
    return Offset((r!['x'] as int).toDouble(), (r['y'] as int).toDouble());
  }

  Future<CreatedWindow> create({
    required String kind,
    required int x,
    required int y,
    required double w,
    required double h,
    bool framed = false,
    bool noActivate = false,
    bool topmost = false,
    bool toolWindow = true,
    String title = '',
    double minW = 0,
    double minH = 0,
    bool darkCaption = true,
    bool acceptFiles = false,
  }) async {
    final r = await _channel.invokeMapMethod<String, Object?>('create', {
      'kind': kind, 'x': x, 'y': y, 'w': w, 'h': h, 'framed': framed, 'noActivate': noActivate, 'topmost': topmost,
      'toolWindow': toolWindow, 'title': title, 'minW': minW, 'minH': minH, 'darkCaption': darkCaption, 'acceptFiles': acceptFiles,
    });
    return CreatedWindow((r!['viewId'] as num).toInt(), (r['hwnd'] as num).toInt());
  }

  Future<void> show(int viewId, {bool activate = true, bool raise = true}) =>
      _channel.invokeMethod('show', {'viewId': viewId, 'activate': activate, 'raise': raise});
  Future<void> hide(int viewId) => _channel.invokeMethod('hide', {'viewId': viewId});
  Future<void> focus(int viewId) => _channel.invokeMethod('focus', {'viewId': viewId});
  Future<void> setTopmost(int viewId, bool on) => _channel.invokeMethod('setTopmost', {'viewId': viewId, 'on': on});
  /// Blurs the desktop behind rounded rectangles (logical px, relative to the window); an empty list switches the blur off.
  Future<void> setBlur(int viewId, List<(Rect, double)> shapes) => _channel.invokeMethod('setBlur', {
        'viewId': viewId,
        'shapes': [
          for (final (r, radius) in shapes) {'x': r.left, 'y': r.top, 'w': r.width, 'h': r.height, 'r': radius},
        ],
      });
  Future<void> setTitle(int viewId, String title) => _channel.invokeMethod('setTitle', {'viewId': viewId, 'title': title});
  Future<void> setDarkCaption(int viewId, bool on) => _channel.invokeMethod('setDarkCaption', {'viewId': viewId, 'on': on});
  Future<void> setBounds(int viewId, {required int x, required int y, required double w, required double h}) =>
      _channel.invokeMethod('setBounds', {'viewId': viewId, 'x': x, 'y': y, 'w': w, 'h': h});

  Future<Map<String, Object?>> getBounds(int viewId) async =>
      (await _channel.invokeMapMethod<String, Object?>('getBounds', {'viewId': viewId}))!;

  Future<String?> pickFile(int viewId, {List<(String, String)> filters = const [], bool save = false, String fileName = ''}) =>
      _channel.invokeMethod<String>('pickFile', {
        'viewId': viewId,
        'filters': [for (final f in filters) [f.$1, f.$2]],
        'save': save,
        'fileName': fileName,
      });

  /// Reserves a strip of a monitor's edge (0 left, 1 top, 2 right, 3 bottom); returns the reserved rectangle.
  Future<Map<String, Object?>> appBar(int viewId, {required int edge, required int size, required MonitorInfo monitor}) async =>
      (await _channel.invokeMapMethod<String, Object?>('appBar', {
        'viewId': viewId, 'edge': edge, 'size': size, 'mx': monitor.x, 'my': monitor.y, 'mw': monitor.w, 'mh': monitor.h,
      }))!;

  Future<void> removeAppBar(int viewId) => _channel.invokeMethod('removeAppBar', {'viewId': viewId});
  Future<void> setNoActivate(int viewId, bool on) => _channel.invokeMethod('setNoActivate', {'viewId': viewId, 'on': on});

  /// DWM live previews of other windows over [viewId]: each item is {hwnd, x, y, w, h (logical, client-relative), opacity}.
  Future<void> thumbnails(int viewId, List<Map<String, Object?>> items) => _channel.invokeMethod('thumbnails', {'viewId': viewId, 'items': items});

  /// Hands a short WAV over to the native side, to be played later by [playSound] (one call per sound, at start-up).
  Future<void> registerSound(int id, Uint8List wav) => _channel.invokeMethod('registerSound', {'id': id, 'bytes': wav});
  Future<void> playSound(int id) => _channel.invokeMethod('playSound', {'id': id});

  /// Lets another process (the engine) bring a window to the front right after a click on one of ours.
  Future<void> allowForeground() => _channel.invokeMethod('allowForeground');

  /// Limits the part of the window that takes the pointer to [rects] (window-relative pixels: {x,y,w,h}); empty: the whole window.
  Future<void> setRegion(int viewId, List<Map<String, int>> rects) => _channel.invokeMethod('setRegion', {'viewId': viewId, 'rects': rects});

  /// Client size in pixels of another window ({w,h}), or an empty map when it is gone.
  Future<Map<String, Object?>> windowSize(int viewId, int hwnd) async =>
      (await _channel.invokeMapMethod<String, Object?>('windowSize', {'viewId': viewId, 'hwnd': hwnd})) ?? const {};
  Future<void> exit() => _channel.invokeMethod('exit');
}

/// The one runner connection of the process. Widget tests replace it.
Native native = Native();
