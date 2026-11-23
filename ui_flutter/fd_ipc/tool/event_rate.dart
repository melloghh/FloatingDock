// Counts the events an engine sends in N seconds, by name (a way to see what keeps the UI busy while idle).
//   dart run tool/event_rate.dart <socket> [seconds]

import 'dart:io';

import 'package:fd_ipc/client.dart';

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'event-rate');
  final seconds = args.length > 1 ? int.parse(args[1]) : 10;
  final counts = <String, int>{}, bytes = <String, int>{};
  c.events.listen((e) {
    counts[e.name] = (counts[e.name] ?? 0) + 1;
    bytes[e.name] = (bytes[e.name] ?? 0) + '${e.data}'.length;
  });
  await c.subscribe(['*']);
  await Future<void>.delayed(Duration(seconds: seconds));
  for (final k in counts.keys.toList()..sort()) {
    print('${k.padRight(28)} ${counts[k]} events, ${bytes[k]} chars');
  }
  await c.close();
  exit(0);
}
