import 'dart:async';

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';

/// What the UI needs from the engine. The real one talks over the socket; tests use [FakeEngineApi].
abstract class EngineApi {
  Future<Object?> call(String method, [Map<String, Object?>? params]);
  Stream<FdEvent> get events;
  bool get connected;
  Stream<bool> get connection;
}

/// Keeps one connection to the engine alive: reconnects with a short back-off, re-subscribes, and tells listeners
/// when the link goes up or down (the UI shows its last state meanwhile).
class SocketEngineApi implements EngineApi {
  SocketEngineApi(this.socketPath, {this.clientName = 'flutter-ui'});

  final String socketPath;
  final String clientName;
  final _events = StreamController<FdEvent>.broadcast();
  final _connection = StreamController<bool>.broadcast();
  FdClient? _client;
  bool _connected = false;
  bool _stopped = false;

  @override
  bool get connected => _connected;
  @override
  Stream<FdEvent> get events => _events.stream;
  @override
  Stream<bool> get connection => _connection.stream;

  /// Connects once and then keeps the link alive in the background.
  Future<void> start() async {
    await _connectOnce();
    unawaited(_supervise());
  }

  Future<bool> _connectOnce() async {
    try {
      final client = await FdClient.connect(socketPath, name: clientName);
      await client.subscribe(['*']);
      client.events.listen(_events.add);
      _client = client;
      _setConnected(true);
      return true;
    } catch (_) {
      return false;
    }
  }

  Future<void> _supervise() async {
    var delay = const Duration(milliseconds: 250);
    while (!_stopped) {
      final client = _client;
      if (client != null) {
        await client.done;
        _client = null;
        _setConnected(false);
        if (_stopped) return;
      }
      if (await _connectOnce()) {
        delay = const Duration(milliseconds: 250);
      } else {
        await Future<void>.delayed(delay);
        if (delay < const Duration(seconds: 3)) delay *= 2;
      }
    }
  }

  void _setConnected(bool value) {
    if (_connected == value) return;
    _connected = value;
    _connection.add(value);
  }

  @override
  Future<Object?> call(String method, [Map<String, Object?>? params]) {
    final client = _client;
    if (client == null) return Future.error(StateError('The engine is not connected.'));
    return client.request(method, params);
  }

  Future<void> stop() async {
    _stopped = true;
    await _client?.close();
  }
}

/// In-memory engine for widget tests and golden renders: answers from fixed data and records every call.
class FakeEngineApi implements EngineApi {
  FakeEngineApi({Map<String, Object?>? settings, List<Map<String, Object?>>? pinned, Map<String, String>? texts})
      : settings = settings ?? {},
        pinned = pinned ?? [],
        texts = texts ?? {};

  final Map<String, Object?> settings;
  final List<Map<String, Object?>> pinned;
  final Map<String, String> texts;

  /// Canned answers for every other method (null when absent).
  final responses = <String, Object?>{};
  final calls = <String>[];
  final _events = StreamController<FdEvent>.broadcast();

  void emit(String name, Object? data) => _events.add(FdEvent(name, data));

  @override
  bool get connected => true;
  @override
  Stream<bool> get connection => const Stream.empty();
  @override
  Stream<FdEvent> get events => _events.stream;

  @override
  Future<Object?> call(String method, [Map<String, Object?>? params]) async {
    calls.add(method);
    switch (method) {
      case Methods.settingsGet:
        return Map<String, Object?>.of(settings);
      case Methods.settingsSet:
        settings.addAll((params!['patch'] as Map).cast<String, Object?>());
        emit(Events.settingsChanged, Map<String, Object?>.of(settings));
        return Map<String, Object?>.of(settings);
      case Methods.pinnedGet:
        return pinned;
      case 'text.table':
        return {'language': settings['Language'], 'table': texts};
      case Methods.windowsList:
        return responses[method] ?? {'foreground': 0, 'windows': <Object?>[]};
      default:
        return responses[method];
    }
  }
}
