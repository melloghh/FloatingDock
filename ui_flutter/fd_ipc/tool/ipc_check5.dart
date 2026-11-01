// Stage 5 link check: Launchpad data and the app actions (launching is not exercised: it would start programs).

import 'dart:io';

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';

int failures = 0;

void check(String name, bool ok, [String detail = '']) {
  if (!ok) failures++;
  print('${ok ? 'PASS' : 'FAIL'}  $name${detail.isEmpty ? '' : '  ($detail)'}');
}

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'ipc-check5');
  final seen = <FdEvent>[];
  c.events.listen(seen.add);
  await c.subscribe(['*']);

  final windir = Platform.environment['WINDIR'];
  for (final exe in ['notepad.exe', 'cmd.exe', 'calc.exe']) {
    final r = await c.request('tiles.add', {'path': '$windir\\System32\\$exe'}) as Map;
    check('tiles.add $exe', r['status'] == 'Added' || r['status'] == 'Duplicate', '${r['status']}');
  }
  await Future<void>.delayed(const Duration(milliseconds: 200));
  check('launchpad.changed events arrive', seen.any((e) => e.name == 'launchpad.changed'));

  final watch = Stopwatch()..start();
  final pad = await c.request('launchpad.apps', {'refresh': false}, const Duration(seconds: 60)) as Map;
  final apps = (pad['apps'] as List).cast<Map>();
  check('launchpad.apps lists the user tiles first', apps.length >= 3 && (pad['userCount'] as num) >= 3 && apps.take(3).every((a) => a['Key'] is String),
      '${apps.length} app(s), ${pad['userCount']} tile(s), ${pad['exportedPins']} Start pin(s), ${watch.elapsedMilliseconds} ms');
  check('every app has a key and a name', apps.every((a) => '${a['Key']}'.isNotEmpty && '${a['Name']}'.isNotEmpty));

  final first = apps.first.cast<String, Object?>();
  final info = await c.request('apps.info', {'app': first}) as Map;
  check('apps.info for a tile', info['isTile'] == true && info['tileIndex'] == 0 && (info['tileCount'] as num) >= 3, '$info');

  await c.request('tiles.move', {'key': first['Key'], 'index': 1});
  final moved = ((await c.request('launchpad.apps')) as Map)['apps'] as List;
  check('tiles.move reorders', (moved[1] as Map)['Key'] == first['Key']);

  final pinned = await c.request('pinned.addApp', {'app': first}) as Map;
  check('pinned.addApp pins a Launchpad app', pinned['added'] == true);
  final after = await c.request('apps.info', {'app': first}) as Map;
  check('apps.info sees it pinned', after['pinned'] == true);
  await c.request('pinned.remove', {'key': first['Key']});

  final reveal = await c.request('apps.reveal', {'app': {'Id': 'x', 'Name': 'Nothing', 'ExePath': r'C:\does\not\exist.exe'}}) as Map;
  check('apps.reveal reports a missing location', reveal['ok'] == false);

  try {
    await c.request('apps.launch', {});
    check('apps.launch needs an app', false);
  } on FdRemoteError catch (e) {
    check('apps.launch needs an app', e.code == ErrorCodes.badParams);
  }

  for (final t in apps.take(3)) {
    await c.request('tiles.remove', {'key': t['Key']});
  }
  await c.request('app.quit');
  await c.done.timeout(const Duration(seconds: 5), onTimeout: () {});
  print(failures == 0 ? 'ALL CHECKS PASSED' : '$failures CHECK(S) FAILED');
  exit(failures == 0 ? 0 : 1);
}
