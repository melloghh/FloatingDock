// Stage 4 link check: the system data behind the top bar menus, read-only (no volume change, no power action, no link opened).

import 'dart:io';

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';

int failures = 0;

void check(String name, bool ok, [String detail = '']) {
  if (!ok) failures++;
  print('${ok ? 'PASS' : 'FAIL'}  $name${detail.isEmpty ? '' : '  ($detail)'}');
}

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'ipc-check4');

  final audio = await c.request('audio.get') as Map;
  check('audio.get', audio['volume'] is num && audio['muted'] is bool, 'volume ${((audio['volume'] as num) * 100).round()}%, muted ${audio['muted']}');

  final net = await c.request('network.get', {'scan': false}, const Duration(seconds: 20)) as Map;
  check('network.get', net['adapters'] is List && net['networks'] is List, '${(net['adapters'] as List).length} adapter(s), ${(net['networks'] as List).length} Wi-Fi network(s)');

  final lang = await c.request('language.list') as Map;
  final layouts = (lang['layouts'] as List).cast<Map>();
  check('language.list', layouts.isNotEmpty && layouts.where((l) => l['current'] == true).length <= 1, layouts.map((l) => l['tag']).join(','));

  final tray = await c.request('tray.items', null, const Duration(seconds: 20)) as Map;
  check('tray.items', tray['items'] is List, '${(tray['items'] as List).length} icon(s)');

  final media = await c.request(Methods.mediaGet);
  check('media.get', media == null || (media as Map)['position'] is num);

  try {
    await c.request('shell.execute', {'uri': 'calc.exe'});
    check('shell.execute refuses arbitrary commands', false);
  } on FdRemoteError catch (e) {
    check('shell.execute refuses arbitrary commands', e.code == ErrorCodes.badParams);
  }
  final ok = await c.request('shell.execute', {'uri': 'ms-settings:sound'});
  check('shell.execute accepts a settings page (not opened in a diagnostic profile)', ok == null);

  final power = await c.request('power.run', {'action': 'restart'}) as Map;
  check('a diagnostic engine never runs power actions', power['ran'] == false);
  try {
    await c.request('power.run', {'action': 'hibernate'});
    check('power.run refuses unknown actions', false);
  } on FdRemoteError catch (e) {
    check('power.run refuses unknown actions', e.code == ErrorCodes.badParams);
  }

  try {
    await c.request('tray.activate', {'index': 999});
    check('tray.activate refuses a vanished icon', false);
  } on FdRemoteError catch (e) {
    check('tray.activate refuses a vanished icon', e.code == ErrorCodes.badParams);
  }

  await c.request('app.quit');
  await c.done.timeout(const Duration(seconds: 5), onTimeout: () {});
  print(failures == 0 ? 'ALL CHECKS PASSED' : '$failures CHECK(S) FAILED');
  exit(failures == 0 ? 0 : 1);
}
