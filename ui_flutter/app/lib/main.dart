import 'dart:async';
import 'dart:io';

import 'package:fd_ipc/protocol.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter/widgets.dart';

import 'app/controller.dart';
import 'windows/dock/dock_view.dart';
import 'app/host.dart';
import 'core/app_state.dart';
import 'core/engine_api.dart';
import 'core/native.dart';
import 'core/sfx.dart';

/// `fd_ui.exe [--socket <endpoint>] [--open-settings [section]]`
///
/// The UI process of FloatingDock: every window (panel, dock, search, menus, Launchpad, settings) is a view of this one
/// Flutter engine. The system-facing work is done by the engine process; this process only talks to it.
Future<void> main(List<String> args) async {
  WidgetsFlutterBinding.ensureInitialized();
  _logFrames();
  final socketIndex = args.indexOf('--socket');
  final socketPath = socketIndex >= 0 && socketIndex + 1 < args.length ? args[socketIndex + 1] : defaultEndpoint();

  final api = SocketEngineApi(socketPath);
  final state = AppState(api);
  final host = WindowHost();
  final controller = AppController(state: state, host: host);

  runWidget(Root(state: state, host: host));

  await api.start();
  await state.refresh();
  controller.start();
  Sfx.enabledOf = () => state.setting<bool>('UiSounds', false);
  Sfx.init();
  final settingsIndex = args.indexOf('--open-settings');
  if (settingsIndex >= 0) {
    final section = settingsIndex + 1 < args.length && !args[settingsIndex + 1].startsWith('--') ? args[settingsIndex + 1] : null;
    unawaited(controller.openSettings(section));
  }

  await controller.exitRequested;
  await native.exit();
}

/// A diagnostic for busy-looking idle: with `FD_FRAME_LOG=<file>` set, the number of frames drawn and the time spent building and
/// rasterizing them is appended to the file every 5 seconds.
void _logFrames() {
  final path = Platform.environment['FD_FRAME_LOG'];
  if (path == null || path.isEmpty) return;
  var frames = 0, buildUs = 0, rasterUs = 0;
  SchedulerBinding.instance.addTimingsCallback((timings) {
    for (final t in timings) {
      frames++;
      buildUs += t.buildDuration.inMicroseconds;
      rasterUs += t.rasterDuration.inMicroseconds;
    }
  });
  Timer.periodic(const Duration(seconds: 5), (_) {
    File(path).writeAsStringSync('${DateTime.now().toIso8601String()} frames=$frames dockTicks=${DockModel.ticks} build=${buildUs ~/ 1000}ms raster=${rasterUs ~/ 1000}ms${Platform.lineTerminator}', mode: FileMode.append);
    frames = buildUs = rasterUs = 0;
    DockModel.ticks = 0;
  });
}
