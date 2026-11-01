import 'dart:async';

import 'package:fd_ipc/protocol.dart';
import 'package:flutter/widgets.dart';

import 'app/controller.dart';
import 'app/host.dart';
import 'core/app_state.dart';
import 'core/engine_api.dart';
import 'core/native.dart';

/// `fd_ui.exe [--socket <path>] [--open-settings [section]]`
///
/// The UI process of FloatingDock: every window (panel, dock, search, menus, Launchpad, settings) is a view of this one
/// Flutter engine. The system-facing work is done by the engine process; this process only talks to it.
Future<void> main(List<String> args) async {
  WidgetsFlutterBinding.ensureInitialized();
  final socketIndex = args.indexOf('--socket');
  final socketPath = socketIndex >= 0 && socketIndex + 1 < args.length ? args[socketIndex + 1] : defaultSocketPath();

  final api = SocketEngineApi(socketPath);
  final state = AppState(api);
  final host = WindowHost();
  final controller = AppController(state: state, host: host);

  runWidget(Root(state: state, host: host));

  await api.start();
  await state.refresh();
  controller.start();
  final settingsIndex = args.indexOf('--open-settings');
  if (settingsIndex >= 0) {
    final section = settingsIndex + 1 < args.length && !args[settingsIndex + 1].startsWith('--') ? args[settingsIndex + 1] : null;
    unawaited(controller.openSettings(section));
  }

  await controller.exitRequested;
  await native.exit();
}
