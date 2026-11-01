// Dart mirror of src/FloatingDock.Ipc/Protocol.cs. The prose description is
// ui_flutter/protocol/PROTOCOL.md. Change all three together.

import 'dart:io';

const int protocolVersion = 1;

/// The longest a Unix socket path may be on Windows.
const int maxSocketPathChars = 100;

abstract final class Methods {
  static const hello = 'hello';
  static const ping = 'ping';
  static const subscribe = 'subscribe';
  static const engineQuit = 'engine.quit';
  static const settingsGet = 'settings.get';
  static const settingsSet = 'settings.set';
  static const pinnedGet = 'pinned.get';
  static const windowsList = 'windows.list';
  static const mediaGet = 'media.get';
  static const mediaControl = 'media.control';
}

abstract final class Events {
  static const settingsChanged = 'settings.changed';
  static const pinnedChanged = 'pinned.changed';
  static const windowsChanged = 'windows.changed';
}

abstract final class ErrorCodes {
  static const badRequest = 'bad-request';
  static const unknownMethod = 'unknown-method';
  static const badParams = 'bad-params';
  static const helloRequired = 'hello-required';
  static const protocolMismatch = 'protocol-mismatch';
  static const failed = 'failed';
}

/// Socket of the engine that serves the current user's real profile (mirrors IpcEndpoint.SocketPathFor).
String defaultSocketPath() {
  final local = Platform.environment['LOCALAPPDATA'];
  if (local == null || local.isEmpty) {
    throw StateError('LOCALAPPDATA is not set.');
  }
  return '$local\\FloatingDock\\engine.sock';
}
