import 'package:fd_ui/windows/launchpad/launchpad_window.dart';
import 'package:fd_ui/windows/popup/context_menu.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'helpers.dart';

List<Map<String, Object?>> sampleApps(int n) => [
      for (var i = 0; i < n; i++)
        {
          'Id': 'a$i',
          'Name': const ['Calculator', 'Zen Browser', 'Visual Studio Code', 'Telegram Desktop', 'Spotify', 'Windows Terminal', 'File Explorer', 'Paint', 'Notepad', 'Settings'][i % 10] + (i >= 10 ? ' ${i ~/ 10}' : ''),
          'Key': 'exe:app$i',
          'IsSeparator': false,
        },
    ];

Future<(Harness, LaunchpadModel)> open(WidgetTester tester, {int apps = 40, Size screen = const Size(1280, 800), bool russian = false}) async {
  final h = await makeHarness(russian: russian);
  h.engine.responses['launchpad.apps'] = {'apps': sampleApps(apps), 'exportedPins': 0, 'userCount': 2};
  final model = LaunchpadModel();
  await pumpWindow(tester, appFor(h.state, LaunchpadView(model: model)), size: screen);
  model.reopen(screen);
  model.visible.value = true;
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 900));
  return (h, model);
}

void main() {
  setUpAll(loadFonts);

  testWidgets('launchpad: the grid of tiles on the scrim', (tester) async {
    await open(tester);
    expect(find.text('Zen Browser'), findsWidgets);
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/launchpad.png'));
  });

  testWidgets('launchpad: Russian header and larger icons', (tester) async {
    final h = await makeHarness(russian: true, settings: {'LaunchpadIconSize': 96, 'Theme': 'Light'});
    h.engine.responses['launchpad.apps'] = {'apps': sampleApps(18), 'exportedPins': 0, 'userCount': 0};
    final model = LaunchpadModel();
    await pumpWindow(tester, appFor(h.state, LaunchpadView(model: model)), size: const Size(1100, 700));
    model.reopen(const Size(1100, 700));
    model.visible.value = true;
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 900));
    expect(find.text('Приложения'), findsOneWidget);
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/launchpad_ru_light.png'));
  });

  testWidgets('launchpad: an empty library says what to do', (tester) async {
    await open(tester, apps: 0, screen: const Size(900, 600));
    expect(find.text('Pin apps in Windows Start or add an application'), findsOneWidget);
  });

  testWidgets('launchpad: a tile launches the app and closes the overlay', (tester) async {
    final (h, model) = await open(tester);
    var closed = 0;
    model.onClose = () => closed++;
    final launched = <Object?>[];
    h.engine.handlers['apps.launch'] = (p) {
      launched.add((p?['app'] as Map)['Name']);
      return null;
    };
    await tester.tap(find.text('Spotify').first);
    await tester.pump();
    expect(launched, ['Spotify']);
    expect(closed, 1);
  });

  testWidgets('launchpad: Escape closes, a click on the scrim closes, a click on the surface does not', (tester) async {
    final (_, model) = await open(tester);
    var closed = 0;
    model.onClose = () => closed++;
    await tester.tapAt(const Offset(500, 100)); // the empty part of the header
    await tester.pump();
    expect(closed, 0, reason: 'inside the surface');
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    expect(closed, 1);
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 300))); // the guard against the opening click uses the real clock
    await tester.tapAt(const Offset(4, 4));
    await tester.pump();
    expect(closed, 2, reason: 'on the scrim');
  });

  testWidgets('launchpad: right click builds the tile menu from the engine facts', (tester) async {
    final (h, model) = await open(tester);
    h.engine.handlers['apps.info'] = (p) => {'pinned': false, 'isTile': true, 'hasOverride': true, 'hasLocation': true, 'tileIndex': 0, 'tileCount': 2};
    List<ContextItem>? shown;
    model.onContextMenu = (items, at) async => shown = items;
    final target = tester.getCenter(find.text('Telegram Desktop').first);
    final gesture = await tester.startGesture(target, kind: PointerDeviceKind.mouse, buttons: kSecondaryMouseButton);
    await gesture.up();
    await tester.pump(const Duration(milliseconds: 50));
    expect(shown, isNotNull);
    final labels = shown!.where((i) => !i.separator).map((i) => i.label).toList();
    expect(labels, ['Open', 'Pin to Dock', 'Remove from Launchpad', 'Move earlier', 'Move later', 'Change icon…', 'Reset icon', 'Open file location']);
    expect(shown!.firstWhere((i) => i.label == 'Move earlier').enabled, isFalse, reason: 'the first tile cannot move earlier');
    expect(shown!.firstWhere((i) => i.label == 'Reset icon').enabled, isTrue);
  });

  testWidgets('launchpad: dropped files are added as tiles and the result is announced', (tester) async {
    final (h, model) = await open(tester);
    final added = <Object?>[];
    h.engine.handlers['tiles.add'] = (p) {
      added.add(p?['path']);
      return {'status': added.length == 1 ? 'Added' : 'Duplicate'};
    };
    model.dropped.add([r'C:\a.lnk', r'C:\b.exe']);
    await tester.pump(const Duration(milliseconds: 100));
    expect(added, [r'C:\a.lnk', r'C:\b.exe']);
    expect(find.textContaining('Added 1'), findsOneWidget);
    expect(find.textContaining('already in Launchpad'), findsOneWidget);
  });
}
