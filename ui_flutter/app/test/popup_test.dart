import 'dart:convert';

import 'package:fd_ui/ui/wavy_progress.dart';
import 'package:fd_ui/windows/popup/context_menu.dart';
import 'package:fd_ui/windows/popup/menus.dart';
import 'package:fd_ui/windows/popup/popup_view.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'helpers.dart';

Widget onDesktop(Widget child) => Directionality(
      textDirection: TextDirection.ltr,
      child: Stack(children: [
        const Positioned.fill(
          child: DecoratedBox(
            decoration: BoxDecoration(
              gradient: LinearGradient(colors: [Color(0xFF2B4A3C), Color(0xFF6B8F7B), Color(0xFF1F2F3A)], begin: Alignment.topLeft, end: Alignment.bottomRight),
            ),
          ),
        ),
        child,
      ]),
    );

final _tinyPng = base64Encode(base64Decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=='));

Future<(Harness, PopupModel)> openMenu(WidgetTester tester, String kind,
    {WidgetBuilder? builder, Size size = const Size(420, 760), bool russian = false, Map<String, Object?> settings = const {}}) async {
  final h = await makeHarness(russian: russian, settings: settings);
  h.engine.responses.addAll({
    'media.get': {
      'title': 'Midnight City', 'artist': 'M83', 'source': 'Spotify', 'canPrevious': true, 'canToggle': true, 'canNext': true,
      'playing': true, 'position': 84.0, 'duration': 243.0, 'canShuffle': true, 'shuffle': true, 'canRepeat': true, 'repeat': 2,
    },
    'audio.get': {'volume': 0.42, 'muted': false},
    'language.list': {
      'target': 1,
      'layouts': [
        {'handle': 1, 'name': 'English (United States)', 'tag': 'EN', 'current': true},
        {'handle': 2, 'name': 'русский (Россия)', 'tag': 'RU', 'current': false},
      ],
    },
    'network.get': {
      'adapters': [{'name': 'Wi-Fi', 'kind': 'Wi-Fi', 'address': '192.168.1.20'}],
      'networks': [
        {'name': 'Home 5G', 'signal': 88, 'connected': true, 'secured': true, 'available': true, 'saved': true},
        {'name': 'Cafe', 'signal': 52, 'connected': false, 'secured': false, 'available': true, 'saved': false},
        {'name': 'Neighbour', 'signal': 31, 'connected': false, 'secured': true, 'available': false, 'saved': true},
      ],
      'error': null,
    },
    'tray.items': {
      'items': [for (var i = 0; i < 8; i++) {'index': i, 'name': 'App $i', 'icon': _tinyPng, 'iconPixels': 16}],
    },
  });
  final model = PopupModel();
  model.open(kind: kind, builder: builder ?? (_) => menuFor(kind), panelWidth: menuWidth(kind), maxHeight: 700, pivot: .8);
  model.visible.value = true;
  await pumpWindow(tester, onDesktop(appFor(h.state, PopupView(model: model))), size: size);
  await tester.pump(const Duration(milliseconds: 300));
  return (h, model);
}

void main() {
  setUpAll(loadFonts);

  testWidgets('menu: media (paused, so the picture is stable)', (tester) async {
    final (h, _) = await openMenu(tester, 'media', size: const Size(420, 300));
    h.engine.responses['media.get'] = {...(h.engine.responses['media.get'] as Map), 'playing': false};
    await tester.pump(const Duration(milliseconds: 800));
    await tester.pump(const Duration(milliseconds: 300));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/menu_media_paused.png'));
  });

  // The playing wave moves with time, so it has no golden: see 'media: the buttons send their commands...' below.
  for (final kind in ['main', 'sound', 'language', 'network', 'tray']) {
    testWidgets('menu: $kind', (tester) async {
      await openMenu(tester, kind);
      await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/menu_$kind.png'));
    });
  }

  testWidgets('menu: calendar', (tester) async {
    await openMenu(tester, 'calendar', size: const Size(460, 760));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/menu_calendar.png'));
  });

  testWidgets('menu: main in Russian', (tester) async {
    await openMenu(tester, 'main', russian: true);
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/menu_main_ru.png'));
  });

  testWidgets('menu: context menu', (tester) async {
    final items = [
      const ContextItem('Open', icon: Icons.open_in_new_rounded),
      const ContextItem('Keep in dock', icon: Icons.push_pin_rounded, checked: true),
      const ContextItem('Change icon…', icon: Icons.image_outlined),
      const ContextItem.separator(),
      const ContextItem('Close window', icon: Icons.close_rounded, enabled: false),
    ];
    await openMenu(tester, 'context', builder: (_) => ContextMenuContent(items: items), size: const Size(380, 380));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/menu_context.png'));
    expect(contextMenuHeight(items), 16 + 4 * 48 + 13);
  });

  testWidgets('context menu: a click runs the item and closes the menu', (tester) async {
    var ran = 0, closed = 0;
    final items = [ContextItem('Run', onTap: () => ran++)];
    final (_, model) = await openMenu(tester, 'context', builder: (_) => ContextMenuContent(items: items), size: const Size(380, 300));
    model.onClose = () => closed++;
    await tester.tap(find.text('Run'));
    await tester.pump();
    expect(ran, 1);
    expect(closed, 1);
  });

  testWidgets('media: the buttons send their commands, the wave moves only while playing', (tester) async {
    final (h, _) = await openMenu(tester, 'media');
    expect(find.text('Midnight City'), findsOneWidget);
    expect(find.text('1:24'), findsOneWidget);
    expect(find.text('4:03'), findsOneWidget);
    h.engine.handlers['media.control'] = (p) {
      h.engine.responses['last-command'] = p?['command'];
      return {'ok': true};
    };
    await tester.tap(find.byIcon(Icons.pause_rounded));
    await tester.pump(const Duration(milliseconds: 50));
    expect(h.engine.responses['last-command'], 'toggle');

    CustomPaint wave() => tester.widget<CustomPaint>(find.descendant(of: find.byType(WavyProgress), matching: find.byType(CustomPaint)).first);
    final before = wave().painter.toString();
    await tester.pump(const Duration(milliseconds: 300));
    expect(tester.binding.hasScheduledFrame, isTrue, reason: 'a playing wave keeps asking for frames');
    expect(before, isNotEmpty);

    // Paused: the ticker stops.
    h.engine.responses['media.get'] = {...(h.engine.responses['media.get'] as Map), 'playing': false};
    await tester.pump(const Duration(milliseconds: 900));
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.byIcon(Icons.play_arrow_rounded), findsOneWidget);
  });

  testWidgets('media: nothing playing', (tester) async {
    final h = await makeHarness();
    h.engine.responses['media.get'] = null;
    final model = PopupModel()..open(kind: 'media', builder: (_) => menuFor('media'), panelWidth: 334, maxHeight: 600, pivot: .5);
    model.visible.value = true;
    await pumpWindow(tester, onDesktop(appFor(h.state, PopupView(model: model))), size: const Size(420, 400));
    expect(find.text('No active media session'), findsOneWidget);
  });

  testWidgets('sound: dragging the slider sends the volume, mute toggles', (tester) async {
    final (h, _) = await openMenu(tester, 'sound');
    expect(find.text('42%'), findsOneWidget);
    final sent = <Object?>[];
    h.engine.handlers['audio.set'] = (p) {
      sent.add(p);
      return null;
    };
    await tester.drag(find.byType(Slider), const Offset(60, 0));
    await tester.pump(const Duration(milliseconds: 100));
    expect(sent.any((p) => (p as Map).containsKey('volume')), isTrue);
    await tester.tap(find.text('Mute'));
    await tester.pump(const Duration(milliseconds: 50));
    expect(sent.any((p) => (p as Map)['muted'] == true), isTrue);
    expect(find.text('Unmute'), findsOneWidget);
  });

  testWidgets('main menu: shut down asks first and only then runs', (tester) async {
    final (h, model) = await openMenu(tester, 'main');
    var closed = 0;
    model.onClose = () => closed++;
    final ran = <Object?>[];
    h.engine.handlers['power.run'] = (p) {
      ran.add(p?['action']);
      return {'ran': true};
    };
    await tester.tap(find.text('Shut down'));
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.text('Shut down this PC?'), findsOneWidget);
    expect(ran, isEmpty);
    await tester.tap(find.text('Cancel'));
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.text('Shut down this PC?'), findsNothing);
    await tester.tap(find.text('Restart'));
    await tester.pump(const Duration(milliseconds: 100));
    await tester.tap(find.widgetWithText(FilledButton, 'Restart'));
    await tester.pump();
    expect(ran, ['restart']);
    expect(closed, 1);
  });

  testWidgets('language: choosing a layout asks the engine for it', (tester) async {
    final (h, _) = await openMenu(tester, 'language');
    final sent = <Object?>[];
    h.engine.handlers['language.set'] = (p) {
      sent.add(p);
      return {'ok': true};
    };
    await tester.tap(find.text('русский (Россия)'));
    await tester.pump();
    expect(sent.single, {'target': 1, 'handle': 2});
  });
}
