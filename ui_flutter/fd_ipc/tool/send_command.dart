// Sends one `ui.command` to a running engine (a way to drive the UI without hotkeys, for the live check).
//   dart run tool/send_command.dart <socket> <command> [menu kind | settings section]

import 'dart:io';

import 'package:fd_ipc/client.dart';

Future<void> main(List<String> args) async {
  final c = await FdClient.connect(args[0], name: 'send-command');
  final data = <String, Object?>{};
  if (args.length > 2) {
    if (args[1] == 'menu') data['kind'] = args[2];
    if (args[1] == 'settings') data['section'] = args[2];
  }
  if (args[1] == 'settings' && args.length > 2) {
    await c.request('ui.command', {'command': 'settings', 'data': {'section': args[2]}});
  } else {
    await c.request('ui.command', {'command': args[1], if (data.isNotEmpty) 'data': data});
  }
  await c.close();
  exit(0);
}
