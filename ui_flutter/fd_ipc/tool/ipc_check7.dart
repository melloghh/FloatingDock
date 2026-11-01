// Stage 7 link check: the dock's model. Nothing is clicked (that would open the Start menu or start programs).

import 'dart:io';

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';

int failures = 0;

void check(String name, bool ok, [String detail = '']) {
  if (!ok) failures++;
  print('${ok ? 'PASS' : 'FAIL'}  $name${detail.isEmpty ? '' : '  ($detail)'}');
}

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'ipc-check7');
  final seen = <FdEvent>[];
  c.events.listen(seen.add);
  await c.subscribe(['*']);

  final windir = Platform.environment['WINDIR'];
  var entries = await c.request('dock.entries') as List;
  check('dock.entries starts with Start and Launchpad', entries.length >= 2 && (entries[0] as Map)['kind'] == 'Start' && (entries[1] as Map)['kind'] == 'Launchpad',
      entries.map((e) => (e as Map)['kind']).join(','));

  await c.request('pinned.add', {'path': '$windir\\System32\\notepad.exe'});
  await c.request('pinned.addSeparator', {});
  await c.request('pinned.add', {'path': '$windir\\System32\\cmd.exe'});
  await Future<void>.delayed(const Duration(milliseconds: 300));
  entries = await c.request('dock.entries') as List;
  final kinds = entries.map((e) => (e as Map)['kind']).toList();
  check('pinned apps and the separator appear as dock items', kinds.contains('Separator') && kinds.where((k) => k == 'App').length >= 2, kinds.join(','));
  check('dock.entries events follow the changes', seen.where((e) => e.name == 'dock.entries').isNotEmpty, '${seen.where((e) => e.name == 'dock.entries').length} event(s)');

  final notepad = entries.cast<Map>().firstWhere((e) => e['kind'] == 'App');
  check('an app entry carries its app and key', notepad['app'] is Map && (notepad['app'] as Map)['Key'] is String && notepad['pinned'] == true);

  final info = await c.request('dock.menuInfo', {'key': notepad['key']}) as Map;
  check('dock.menuInfo', info['found'] == true && info['pinned'] == true && (info['pinnedCount'] as num) >= 3, '$info');

  final key = notepad['key'];
  await c.request('pinned.reorder', {'key': key, 'index': 2});
  final moved = (await c.request('dock.entries') as List).cast<Map>().where((e) => e['pinned'] == true).map((e) => e['key']).toList();
  check('pinned.reorder moves an item', moved.indexOf(key) == 2, moved.join(','));

  final anchors = await c.request('dock.anchors', {
    'anchors': [
      {'key': key, 'left': 100, 'top': 900, 'right': 148, 'bottom': 948},
    ],
    'fallback': {'left': 500, 'top': 900, 'right': 548, 'bottom': 948},
  });
  check('dock.anchors is accepted', anchors == null);

  final space = await c.request('space.get') as Map;
  final mon = (space['monitors'] as List).cast<Map>();
  check('space.get reports every monitor', mon.isNotEmpty && mon.every((m) => m.containsKey('hide') && m.containsKey('blockReveal')), '$space');

  try {
    await c.request('dock.activate', {'key': 'no-such-item'});
    check('activating a vanished item is refused', false);
  } on FdRemoteError catch (e) {
    check('activating a vanished item is refused', e.code == ErrorCodes.badParams);
  }

  for (final e in (await c.request('dock.entries') as List).cast<Map>().where((e) => e['pinned'] == true)) {
    await c.request('pinned.remove', {'key': e['key']});
  }
  await c.request('app.quit');
  await c.done.timeout(const Duration(seconds: 5), onTimeout: () {});
  print(failures == 0 ? 'ALL CHECKS PASSED' : '$failures CHECK(S) FAILED');
  exit(failures == 0 ? 0 : 1);
}
