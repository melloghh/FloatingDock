import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'protocol.dart';

/// The engine answered a request with {"error": {...}}.
class FdRemoteError implements Exception {
  FdRemoteError(this.code, this.message);
  final String code;
  final String message;
  @override
  String toString() => 'FdRemoteError($code): $message';
}

/// An event pushed by the engine, e.g. `settings.changed`.
class FdEvent {
  FdEvent(this.name, this.data);
  final String name;
  final Object? data;
}

/// One connection to the engine. Requests are matched to replies by id; events arrive on [events].
class FdClient {
  FdClient._(this._socket) {
    _socket
        .cast<List<int>>()
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(_onLine, onDone: _onClosed, onError: (_) => _onClosed(), cancelOnError: true);
  }

  final Socket _socket;
  final _pending = <int, Completer<Object?>>{};
  final _events = StreamController<FdEvent>.broadcast();
  final _closed = Completer<void>();
  int _nextId = 0;

  /// Result of `hello`: {protocol, engine, pid}.
  late final Map<String, Object?> hello;

  Stream<FdEvent> get events => _events.stream;

  /// Completes when the connection ends (the engine quit, or the socket broke).
  Future<void> get done => _closed.future;

  static Future<FdClient> connect(String socketPath, {String name = 'flutter-ui', Duration timeout = const Duration(seconds: 5)}) async {
    if (socketPath.length > maxSocketPathChars) {
      throw ArgumentError('Socket path is too long: $socketPath');
    }
    final socket = await Socket.connect(InternetAddress(socketPath, type: InternetAddressType.unix), 0).timeout(timeout);
    final client = FdClient._(socket);
    client.hello = ((await client.request(Methods.hello, {'client': name, 'protocol': protocolVersion, 'pid': pid}, timeout)) as Map).cast<String, Object?>();
    return client;
  }

  Future<Object?> request(String method, [Map<String, Object?>? params, Duration timeout = const Duration(seconds: 10)]) {
    final id = ++_nextId;
    final completer = Completer<Object?>();
    _pending[id] = completer;
    _socket.write('${jsonEncode({'id': id, 'method': method, 'params': params ?? <String, Object?>{}})}\n');
    return completer.future.timeout(timeout, onTimeout: () {
      _pending.remove(id);
      throw TimeoutException('$method did not answer', timeout);
    });
  }

  Future<void> subscribe(List<String> topics) => request(Methods.subscribe, {'topics': topics});

  Future<void> close() async {
    await _socket.close();
    _socket.destroy();
  }

  void _onLine(String line) {
    final msg = jsonDecode(line);
    if (msg is! Map) return;
    final event = msg['event'];
    if (event is String) {
      _events.add(FdEvent(event, msg['data']));
      return;
    }
    final id = msg['id'];
    final completer = id is int ? _pending.remove(id) : null;
    if (completer == null) return;
    final error = msg['error'];
    if (error is Map) {
      completer.completeError(FdRemoteError('${error['code']}', '${error['message']}'));
    } else {
      completer.complete(msg['result']);
    }
  }

  void _onClosed() {
    for (final c in _pending.values) {
      c.completeError(const SocketException('The engine closed the connection.'));
    }
    _pending.clear();
    if (!_closed.isCompleted) _closed.complete();
    _events.close();
  }
}
