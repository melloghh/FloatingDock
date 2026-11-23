// Prints media.get every 500 ms (position, duration, playing) so a player's timeline can be checked against reality.
//   dart run tool/media_probe.dart <socket> [seconds]

import 'dart:io';

import 'package:fd_ipc/client.dart';

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'media-probe');
  final seconds = args.length > 1 ? int.parse(args[1]) : 20;
  final command = args.length > 2 ? args[2] : null; // e.g. toggle: sent once, after the first second
  var sent = false;
  final started = DateTime.now();
  while (DateTime.now().difference(started).inSeconds < seconds) {
    final m = await c.request('media.get') as Map?;
    final t = DateTime.now().difference(started).inMilliseconds / 1000;
    print('${t.toStringAsFixed(1).padLeft(5)}s  ${m == null ? 'no session' : 'playing=${m['playing']}  pos=${(m['position'] as num).toStringAsFixed(2)}  dur=${(m['duration'] as num).toStringAsFixed(1)}'}');
    if (command != null && !sent && t > 1) {
      sent = true;
      final r = await c.request('media.control', {'command': command}) as Map;
      print('-- media.control $command: ${r['ok']}');
    }
    await Future<void>.delayed(const Duration(milliseconds: 500));
  }
  await c.close();
  exit(0);
}
