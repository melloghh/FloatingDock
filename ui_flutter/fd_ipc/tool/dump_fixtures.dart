// Writes the engine's real default settings and Russian text table to ui_flutter/app/test/fixtures, so UI tests render
// with exactly the data the engine serves. Run through tools/with-engine.ps1; the engine is left running (the next script quits it).

import 'dart:convert';
import 'dart:io';

import 'package:fd_ipc/client.dart';
import 'package:fd_ipc/protocol.dart';

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'dump-fixtures');
  final dir = Directory('../app/test/fixtures')..createSync(recursive: true);
  const pretty = JsonEncoder.withIndent('  ');
  final settings = await c.request(Methods.settingsGet);
  File('${dir.path}/settings.json').writeAsStringSync(pretty.convert(settings));
  await c.request(Methods.settingsSet, {'patch': {'Language': 'Russian'}});
  final texts = await c.request('text.table');
  File('${dir.path}/texts_ru.json').writeAsStringSync(pretty.convert((texts as Map)['table']));
  print('wrote settings.json and texts_ru.json (${(texts['table'] as Map).length} strings)');
  await c.request(Methods.settingsSet, {'patch': {'Language': 'English'}});
  await c.close();
  exit(0);
}
