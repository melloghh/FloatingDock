// Stage 2 link check: the settings-window handlers (reset, texts, app info, pinned apps, tiles, icons, packs, hotkeys).
//   dart run tool/ipc_check2.dart <socket path> <profile dir>
// The engine must run with --diagnostic-profile; this script ends by asking it to quit.

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
  final c = await FdClient.connect(args[0], name: 'ipc-check2');
  final profile = args[1];
  final seen = <FdEvent>[];
  c.events.listen(seen.add);
  await c.subscribe(['*']);

  final info = await c.request('app.info') as Map;
  check('app.info', info['dataPath'] == profile && info['diagnostic'] == true, 'v${info['version']}');

  var settings = await c.request(Methods.settingsGet) as Map;
  check('diagnostic profile leaves the taskbar alone', settings['TaskbarMode'] == 'Leave' && settings['StartWithWindows'] == false);

  await c.request(Methods.settingsSet, {'patch': {'Language': 'Russian'}});
  final texts = await c.request('text.table') as Map;
  check('text.table in Russian', texts['language'] == 'Russian' && (texts['table'] as Map)['Dock position'] != null, '${(texts['table'] as Map).length} strings');
  await c.request(Methods.settingsSet, {'patch': {'Language': 'English'}});
  final english = await c.request('text.table') as Map;
  check('text.table is empty for English', (english['table'] as Map).isEmpty);

  await c.request(Methods.settingsSet, {'patch': {'IconSize': 33, 'Theme': 'Light'}});
  settings = await c.request('settings.reset') as Map;
  check('settings.reset restores defaults', settings['IconSize'] == 48 && settings['Theme'] == 'Dark');
  // The diagnostic engine must not act on the reset TaskbarMode (default HideCompletely): its log must not say it did.
  await Future<void>.delayed(const Duration(milliseconds: 300));
  final log = File('$profile\\Logs\\engine.log').readAsStringSync();
  check('diagnostic engine never hides the Windows taskbar', !log.contains('applied HideCompletely'));

  final hotkeys = await c.request('hotkey.status') as Map;
  check('hotkey.status', hotkeys.containsKey('search') && hotkeys.containsKey('launchpad'));

  // Pinned apps: add a copy of a real .lnk-less exe through the importer, move, remove.
  final notepad = '${Platform.environment['WINDIR']}\\System32\\notepad.exe';
  final added = await c.request('pinned.add', {'path': notepad}) as Map;
  check('pinned.add imports an exe', added['status'] == 'Added', '${added['status']} ${added['message'] ?? ''}');
  final calc = '${Platform.environment['WINDIR']}\\System32\\mspaint.exe';
  await c.request('pinned.add', {'path': calc});
  var pinned = await c.request(Methods.pinnedGet) as List;
  check('pinned.get returns keys', pinned.length >= 2 && pinned.every((a) => (a as Map)['Key'] is String), '${pinned.length} app(s)');
  final firstKey = (pinned.first as Map)['Key'];
  await c.request('pinned.move', {'key': firstKey, 'index': 1});
  pinned = await c.request(Methods.pinnedGet) as List;
  check('pinned.move reorders', (pinned[1] as Map)['Key'] == firstKey);
  await Future<void>.delayed(const Duration(milliseconds: 200));
  check('pinned.changed events arrive', seen.where((e) => e.name == Events.pinnedChanged).length >= 2, '${seen.where((e) => e.name == Events.pinnedChanged).length}');
  final dup = await c.request('pinned.add', {'path': notepad}) as Map;
  check('pinned.add refuses a duplicate', dup['status'] == 'Duplicate');
  final bad = await c.request('pinned.add', {'path': '${Platform.environment['WINDIR']}\\win.ini'}) as Map;
  check('pinned.add refuses a non-app file', bad['status'] == 'Unsupported', '${bad['status']}');

  // Icons: the engine resolves a PNG/ICO for a pinned app.
  final app = (pinned.first as Map).cast<String, Object?>();
  final icon = await c.request('icons.get', {'app': app, 'size': 48}) as Map;
  final bytes = icon['image'] is String ? base64Decode(icon['image'] as String) : null;
  check('icons.get returns image bytes', bytes != null && bytes.length > 200, '${bytes?.length} bytes, magic ${bytes?.take(4).toList()}');

  // Per-app icon override with a real PNG written by this script.
  final png = base64Decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==');
  final pngFile = File('$profile\\one.png')..writeAsBytesSync(png);
  try {
    await c.request('icons.override', {'key': firstKey, 'path': pngFile.path});
    final list = await c.request('appicons.list') as List;
    check('icons.override sets an override', list.any((a) => (a as Map)['Key'] == firstKey && a['hasOverride'] == true));
    await Future<void>.delayed(const Duration(milliseconds: 200));
    check('icons.changed event arrives', seen.any((e) => e.name == 'icons.changed'));
    await c.request('icons.reset', {'key': firstKey});
    final after = await c.request('appicons.list') as List;
    check('icons.reset removes it', after.every((a) => (a as Map)['hasOverride'] != true));
  } on FdRemoteError catch (e) {
    check('icons.override', false, '$e');
  }

  // Packs.
  final packs = await c.request('packs.list') as List;
  check('packs.list has the built-in packs', packs.any((p) => (p as Map)['id'] == 'Default'), packs.map((p) => (p as Map)['id']).join(', '));
  try {
    await c.request('packs.inspect', {'path': '${Platform.environment['WINDIR']}\\win.ini'});
    check('packs.inspect refuses a non-zip', false);
  } on FdRemoteError catch (e) {
    check('packs.inspect refuses a non-zip', e.code == ErrorCodes.badParams, e.message);
  }

  await c.request('pinned.remove', {'key': firstKey});
  pinned = await c.request(Methods.pinnedGet) as List;
  check('pinned.remove', pinned.every((a) => (a as Map)['Key'] != firstKey));

  final tiles = await c.request('tiles.get') as List;
  check('tiles.get', tiles is List);

  await c.request('app.quit');
  final closed = await c.done.timeout(const Duration(seconds: 5), onTimeout: () => throw 'still open').then((_) => true).catchError((_) => false);
  check('app.quit closes the connection', closed);
  print(failures == 0 ? 'ALL CHECKS PASSED' : '$failures CHECK(S) FAILED');
  exit(failures == 0 ? 0 : 1);
}
