// Stage 1 link check: talks to a running engine and verifies every kind of message.
//   dart run tool/ipc_check.dart <socket path> <profile dir>
// Use an engine started with --diagnostic-profile <profile dir>: this script changes settings and finally asks the engine to quit.

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';

int failures = 0;

void check(String name, bool ok, [String detail = '']) {
  if (!ok) failures++;
  print('${ok ? 'PASS' : 'FAIL'}  $name${detail.isEmpty ? '' : '  ($detail)'}');
}

Future<void> main(List<String> args) async {
  final socketPath = args[0];
  final profile = args[1];
  final c = await FdClient.connect(socketPath, name: 'ipc-check');
  check('hello', c.hello['protocol'] == protocolVersion, 'engine ${c.hello['engine']}, pid ${c.hello['pid']}');

  final pong = await c.request(Methods.ping) as Map;
  check('ping', pong['time'] is int);

  final seen = <FdEvent>[];
  c.events.listen(seen.add);
  await c.subscribe(['settings', 'pinned', 'windows']);

  final settings = await c.request(Methods.settingsGet) as Map;
  check('settings.get', settings['IconSize'] is int && settings['AccentSeed'] is String && settings['Edge'] is String,
      'IconSize=${settings['IconSize']} AccentSeed=${settings['AccentSeed']} Edge=${settings['Edge']}');

  final after = await c.request(Methods.settingsSet, {
    'patch': {'AccentSeed': '5F6368', 'IconSize': 40, 'Language': 'Russian', 'TilingKeybindings': {'focus-left': 'Alt+H'}},
  }) as Map;
  check('settings.set returns the new settings',
      after['AccentSeed'] == '5F6368' && after['IconSize'] == 40 && after['Language'] == 'Russian' && (after['TilingKeybindings'] as Map)['focus-left'] == 'Alt+H');
  await Future<void>.delayed(const Duration(milliseconds: 300));
  final changed = seen.where((e) => e.name == Events.settingsChanged).toList();
  check('settings.changed event arrives', changed.isNotEmpty && (changed.last.data as Map)['IconSize'] == 40, '${changed.length} event(s)');

  for (final bad in [
    ({'IconSize': 'huge'}, 'IconSize'),
    ({'NoSuchSetting': 1}, 'NoSuchSetting'),
    ({'Edge': 'Top'}, 'Edge'),
    ({'SchemaVersion': 9}, 'SchemaVersion'),
  ]) {
    try {
      await c.request(Methods.settingsSet, {'patch': bad.$1});
      check('settings.set rejects ${bad.$2}', false, 'no error');
    } on FdRemoteError catch (e) {
      check('settings.set rejects ${bad.$2}', e.code == ErrorCodes.badParams && e.message.startsWith(bad.$2), e.toString());
    }
  }
  final unchanged = await c.request(Methods.settingsGet) as Map;
  check('rejected patches changed nothing', unchanged['IconSize'] == 40 && unchanged['Edge'] == after['Edge']);

  try {
    await c.request('no.such.method');
    check('unknown method is an error', false);
  } on FdRemoteError catch (e) {
    check('unknown method is an error', e.code == ErrorCodes.unknownMethod);
  }

  final pinned = await c.request(Methods.pinnedGet);
  check('pinned.get', pinned is List, '${(pinned as List).length} app(s)');

  final windows = await c.request(Methods.windowsList) as Map;
  final list = windows['windows'] as List;
  check('windows.list', list.isNotEmpty && list.every((w) => (w as Map)['hwnd'] is int), '${list.length} window(s), foreground ${windows['foreground']}');

  final media = await c.request(Methods.mediaGet) as Map?;
  check('media.get', media == null || media['title'] is String, media == null ? 'nothing playing' : '"${media['title']}"');

  // Round-trip latency: 300 sequential pings.
  final watch = Stopwatch()..start();
  for (var i = 0; i < 300; i++) {
    await c.request(Methods.ping);
  }
  watch.stop();
  final avgMs = watch.elapsedMicroseconds / 300 / 1000;
  check('ping round trip', avgMs < 5, '${avgMs.toStringAsFixed(3)} ms average');

  // 200 requests in flight at once, answered out of order.
  final many = await Future.wait(List.generate(200, (_) => c.request(Methods.settingsGet)));
  check('200 concurrent requests', many.length == 200 && many.every((m) => (m as Map)['IconSize'] == 40));

  // The change must reach disk (debounced ~400 ms).
  await Future<void>.delayed(const Duration(milliseconds: 900));
  final file = File('$profile\\config.json');
  final onDisk = file.existsSync() ? jsonDecode(file.readAsStringSync()) as Map : const {};
  check('settings persisted to config.json', onDisk['IconSize'] == 40 && onDisk['AccentSeed'] == '5F6368', file.path);

  await c.request(Methods.engineQuit);
  final closed = await c.done.timeout(const Duration(seconds: 5), onTimeout: () => throw 'connection stayed open after quit').then((_) => true).catchError((_) => false);
  check('engine.quit closes the connection', closed);

  print(failures == 0 ? 'ALL CHECKS PASSED' : '$failures CHECK(S) FAILED');
  exit(failures == 0 ? 0 : 1);
}
