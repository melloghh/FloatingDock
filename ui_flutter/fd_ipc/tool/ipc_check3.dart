// Stage 3 link check: search against the real installed-apps catalogue (read-only: nothing is launched).

import 'dart:io';

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';

int failures = 0;

void check(String name, bool ok, [String detail = '']) {
  if (!ok) failures++;
  print('${ok ? 'PASS' : 'FAIL'}  $name${detail.isEmpty ? '' : '  ($detail)'}');
}

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'ipc-check3');
  final idle = await c.request('search.query', {'text': ''}) as Map;
  check('empty query without history is idle', idle['status'] == 'idle' && (idle['results'] as List).isEmpty);

  final watch = Stopwatch()..start();
  final first = await c.request('search.query', {'text': 'notepad'}, const Duration(seconds: 30)) as Map;
  final coldMs = watch.elapsedMilliseconds;
  final results = (first['results'] as List).cast<Map>();
  check('search finds an app by name', results.any((r) => '${r['name']}'.toLowerCase().contains('notepad') && r['kind'] == 'App' && r['app'] is Map),
      '${results.length} result(s), first read of the catalogue ${coldMs} ms');

  watch..reset()..start();
  await c.request('search.query', {'text': 'notepad'});
  check('a repeated search is served from the cached index', watch.elapsedMilliseconds < 150, '${watch.elapsedMilliseconds} ms');

  final cmd = await c.request('search.query', {'text': 'launchp'}) as Map;
  check('commands are searchable', (cmd['results'] as List).any((r) => (r as Map)['id'] == 'cmd:launchpad'));

  final none = await c.request('search.query', {'text': 'qzqzqzqz'}) as Map;
  check('no match says none', none['status'] == 'none');

  try {
    await c.request('search.open', {'id': 'app:exe:does-not-exist'});
    check('opening a vanished result is refused', false);
  } on FdRemoteError catch (e) {
    check('opening a vanished result is refused', e.code == ErrorCodes.badParams);
  }

  final seen = <FdEvent>[];
  c.events.listen(seen.add);
  await c.subscribe(['ui']);
  await c.request('search.open', {'id': 'cmd:settings'});
  await Future<void>.delayed(const Duration(milliseconds: 200));
  check('the settings command reaches the UI as ui.command',
      seen.any((e) => e.name == 'ui.command' && (e.data as Map)['command'] == 'settings'));

  await c.request('app.quit');
  await c.done.timeout(const Duration(seconds: 5), onTimeout: () {});
  print(failures == 0 ? 'ALL CHECKS PASSED' : '$failures CHECK(S) FAILED');
  exit(failures == 0 ? 0 : 1);
}
