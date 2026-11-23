import 'dart:convert';
import 'dart:io';

import 'package:fd_ipc/protocol.dart';
import 'package:fd_ipc/client.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:fd_ui/core/app_state.dart';
import 'package:fd_ui/core/engine_api.dart';
import 'package:fd_ui/core/theme.dart';

/// Real fonts instead of the test font, so goldens look like the app: Cascadia Mono for text. (Material Icons come with the SDK.)
Future<void> loadFonts() async {
  final loader = FontLoader(fontFamily);
  final file = File('C:/Windows/Fonts/CascadiaMono.ttf');
  if (!file.existsSync()) return;
  loader.addFont(Future.value(ByteData.sublistView(file.readAsBytesSync())));
  await loader.load();
  final icons = File('C:/dev/flutter/bin/cache/artifacts/material_fonts/MaterialIcons-Regular.otf');
  if (icons.existsSync()) {
    final iconLoader = FontLoader('MaterialIcons')..addFont(Future.value(ByteData.sublistView(icons.readAsBytesSync())));
    await iconLoader.load();
  }
}

Map<String, Object?> defaultSettings() =>
    (jsonDecode(File('test/fixtures/settings.json').readAsStringSync()) as Map).cast<String, Object?>();

Map<String, String> russianTexts() =>
    (jsonDecode(File('test/fixtures/texts_ru.json').readAsStringSync()) as Map).cast<String, String>();

/// A realistic pinned list: names and ids only (the fake engine returns no pictures, so icons stay placeholders).
List<Map<String, Object?>> samplePinned() => [
      for (final (i, name) in const ['File Explorer', 'Zen Browser', 'Visual Studio Code', 'Telegram', 'Spotify', 'Terminal'].indexed)
        {'Id': 'app$i', 'Name': name, 'ExePath': 'C:\\Apps\\$name.exe', 'Key': 'exe:c:\\apps\\${name.toLowerCase()}.exe', 'IsSeparator': false},
    ];

class Harness {
  Harness(this.engine, this.state);
  final FakeEngineApi engine;
  final AppState state;
}

Future<Harness> makeHarness({Map<String, Object?>? settings, bool russian = false}) async {
  final merged = {...defaultSettings(), ...?settings};
  if (russian) merged['Language'] = 'Russian';
  final engine = FakeEngineApi(settings: merged, pinned: samplePinned(), texts: russian ? russianTexts() : {});
  engine.responses.addAll({
    'app.info': {'version': '1.6.6', 'dataPath': r'C:\Users\main\AppData\Local\FloatingDock', 'logsPath': '', 'packsPath': ''},
    'hotkey.status': {'launchpad': true, 'search': false, 'restoreTaskbar': true, 'dockFocus': true},
    'packs.list': [
      {'id': 'Default', 'folder': 'Default', 'name': 'System icons', 'iconCount': 0, 'isBuiltIn': true, 'description': '', 'version': '', 'warnings': []},
      {'id': 'Custom', 'folder': 'Custom', 'name': 'My icons', 'iconCount': 3, 'isBuiltIn': true, 'description': '', 'version': '', 'warnings': []},
      {'id': 'wolf', 'folder': 'Wolf', 'name': 'Wolf Icons', 'iconCount': 142, 'isBuiltIn': false, 'description': 'Rounded colourful icons', 'version': '1.2', 'warnings': []},
    ],
    'tiles.get': [
      {'Id': 't1', 'Name': 'Calculator', 'Key': 'lnk:calc', 'IsSeparator': false},
    ],
    'appicons.list': [for (final a in samplePinned().take(4)) {...a, 'hasOverride': a['Name'] == 'Telegram'}],
    'pinned.get': samplePinned(),
  });
  final state = AppState(engine, systemBrightness: Brightness.dark);
  await state.refresh();
  return Harness(engine, state);
}

/// The app's widget tree around [child], as a window shows it.
Widget appFor(AppState state, Widget child) => AppScope(
      state: state,
      child: ListenableBuilder(
        listenable: state,
        builder: (context, _) => MaterialApp(
          debugShowCheckedModeBanner: false,
          theme: state.theme,
          home: child,
        ),
      ),
    );

Future<void> pumpWindow(WidgetTester tester, Widget app, {Size size = const Size(1040, 760)}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  await tester.pumpWidget(app);
  // Let reveals, glides and futures finish.
  await tester.pump(const Duration(milliseconds: 50));
  await tester.pump(const Duration(milliseconds: 1500));
}

/// Silence the unused import of protocol/client in helpers that tests extend.
void keep() {
  Methods.ping;
  FdClient;
}
