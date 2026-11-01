// Stage 6 link check: the data behind the top bar.

import 'dart:io';

import 'package:fd_ipc/client.dart';

int failures = 0;

void check(String name, bool ok, [String detail = '']) {
  if (!ok) failures++;
  print('${ok ? 'PASS' : 'FAIL'}  $name${detail.isEmpty ? '' : '  ($detail)'}');
}

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'ipc-check6');

  final battery = await c.request('battery.get') as Map;
  check('battery.get', battery['present'] is bool, '$battery');

  final online = await c.request('network.available');
  check('network.available', online is bool, '$online');

  final lang = await c.request('language.current') as Map;
  check('language.current is a two-letter tag', RegExp(r'^[A-Z]{2,3}$').hasMatch('${lang['tag']}'), '${lang['tag']}');

  final monitors = await c.request('monitors.get') as List;
  check('monitors.get', monitors.isNotEmpty && monitors.any((m) => (m as Map)['primary'] == true), '${monitors.length} monitor(s), scale ${(monitors.first as Map)['scale']}');

  final full = await c.request('fullscreen.get') as Map;
  check('fullscreen.get has one entry per monitor', (full['monitors'] as List).length == monitors.length);

  final windows = await c.request('windows.list') as Map;
  check('windows.list names the foreground app', windows.containsKey('foregroundName'), '${windows['foregroundName']}');

  await c.request('app.quit');
  await c.done.timeout(const Duration(seconds: 5), onTimeout: () {});
  print(failures == 0 ? 'ALL CHECKS PASSED' : '$failures CHECK(S) FAILED');
  exit(failures == 0 ? 0 : 1);
}
