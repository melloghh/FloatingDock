// Changes settings of a running engine (a way to try a setting live without opening the settings window).
//   dart run tool/set_setting.dart <socket> TopBarStyle=Island ShowWindowPreviews=false
// Values are read as true/false, a number, or else text (no JSON, so PowerShell 5.1 cannot mangle the quotes).

import 'dart:io';

import 'package:fd_ipc/client.dart';

Future<void> main(List<String> args) async {
  final patch = <String, Object?>{};
  for (final pair in args.skip(1)) {
    final i = pair.indexOf('=');
    final raw = pair.substring(i + 1);
    patch[pair.substring(0, i)] = raw == 'true' ? true : raw == 'false' ? false : num.tryParse(raw) ?? raw;
  }
  final c = await FdClient.connect(args[0], name: 'set-setting');
  await c.request('settings.set', {'patch': patch});
  await c.close();
  exit(0);
}
