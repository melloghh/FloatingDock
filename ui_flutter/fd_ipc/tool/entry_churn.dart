// Prints what changes between two consecutive dock.entries events (which key, which field), for N seconds.
//   dart run tool/entry_churn.dart <socket> [seconds]

import 'dart:convert';
import 'dart:io';

import 'package:fd_ipc/client.dart';

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'entry-churn');
  final seconds = args.length > 1 ? int.parse(args[1]) : 15;
  Map<String, String>? last;
  c.events.listen((e) {
    if (e.name != 'dock.entries') return;
    final now = <String, String>{};
    for (final m in (e.data as List).cast<Map>()) {
      final windows = (m['windows'] as List).cast<Map>();
      final copy = Map<String, Object?>.of(m.cast<String, Object?>())..remove('windows');
      now['${m['key']}'] = '${jsonEncode(copy)} ${windows.map((w) => '${w['hwnd']}:${w['minimized']}:${w['title']}').join('|')}';
    }
    if (last != null) {
      for (final k in {...now.keys, ...last!.keys}) {
        if (now[k] != last![k]) print('${DateTime.now().toIso8601String().substring(11, 23)} $k\n   was ${last![k]}\n   now ${now[k]}');
      }
    }
    last = now;
  });
  await c.subscribe(['*']);
  await Future<void>.delayed(Duration(seconds: seconds));
  await c.close();
  exit(0);
}
