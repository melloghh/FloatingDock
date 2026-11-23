// Calls one engine method and prints the reply (a way to try an engine request by hand).
//   dart run tool/call.dart <socket> <method> [key=value ...]     values: true/false, a number, or else text
import 'dart:convert';
import 'dart:io';

import 'package:fd_ipc/client.dart';

Future<void> main(List<String> args) async {
  final params = <String, Object?>{};
  for (final pair in args.skip(2)) {
    final i = pair.indexOf('=');
    final raw = pair.substring(i + 1);
    params[pair.substring(0, i)] = raw == 'true' ? true : raw == 'false' ? false : num.tryParse(raw) ?? raw;
  }
  final c = await FdClient.connect(args[0], name: 'call');
  final r = await c.request(args[1], params);
  print(const JsonEncoder.withIndent('  ').convert(r));
  await c.close();
  exit(0);
}
