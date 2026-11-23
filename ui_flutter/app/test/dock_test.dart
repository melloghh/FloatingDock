import 'package:fd_ui/windows/dock/dock_math.dart';
import 'package:fd_ui/windows/dock/dock_view.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'helpers.dart';

List<DockEntry> sampleEntries() => [
      DockEntry(key: 'special:start', kind: DockEntryKind.start, name: 'Start', app: {'Id': 'special-start', 'Name': 'Start'}),
      DockEntry(key: 'special:launchpad', kind: DockEntryKind.launchpad, name: 'Launchpad', app: {'Id': 'special-launchpad', 'Name': 'Launchpad'}),
      for (final (i, n) in const ['File Explorer', 'Zen Browser', 'Visual Studio Code', 'Telegram', 'Spotify'].indexed)
        DockEntry(
          key: 'app$i', kind: DockEntryKind.app, name: n, pinned: true, running: i != 3, active: i == 1,
          app: {'Id': 'a$i', 'Name': n, 'Key': 'app$i'},
          windows: i == 3 ? const [] : [DockWindowInfo({'hwnd': 100 + i, 'title': '$n window'})],
        ),
      DockEntry(key: 'special:separator', kind: DockEntryKind.separator),
      DockEntry(key: 'run1', kind: DockEntryKind.app, name: 'Notepad', running: true, app: {'Id': 'r1', 'Name': 'Notepad', 'Key': 'run1'}, windows: [DockWindowInfo({'hwnd': 200, 'title': 'Untitled'})]),
    ];

Future<(Harness, DockModel)> openDock(WidgetTester tester, {Map<String, Object?> settings = const {}, List<DockEntry>? entries}) async {
  final h = await makeHarness(settings: {'Theme': 'Dark', ...settings});
  final model = DockModel(state: h.state);
  model.setEntries(entries ?? sampleEntries());
  final size = model.windowSize;
  await pumpWindow(
    tester,
    Directionality(
      textDirection: TextDirection.ltr,
      child: Stack(children: [
        const Positioned.fill(
          child: DecoratedBox(
            decoration: BoxDecoration(gradient: LinearGradient(colors: [Color(0xFF2B4A3C), Color(0xFF8DAE9B), Color(0xFF1F2F3A)], begin: Alignment.topLeft, end: Alignment.bottomRight)),
          ),
        ),
        appFor(h.state, DockView(model: model)),
      ]),
    ),
    size: size,
  );
  await settle(tester, 700);
  return (h, model);
}

/// Pumps real frames (one per 16 ms): the dock animates per frame, a single long pump is one frame.
Future<void> settle(WidgetTester tester, int ms) async {
  for (var t = 0; t < ms; t += 16) {
    await tester.pump(const Duration(milliseconds: 16));
  }
}

Future<TestGesture> hoverAt(WidgetTester tester, DockModel model, double along) async {
  final g = await tester.createGesture(kind: PointerDeviceKind.mouse);
  await g.addPointer(location: const Offset(1, 1));
  final y = model.axis.point(along, model.metrics.plateTop + model.gp.dockHeight / 2);
  await g.moveTo(y);
  await settle(tester, 700);
  return g;
}

void main() {
  setUpAll(loadFonts);

  testWidgets('dock: at rest, centred in its window with a running dot under the apps', (tester) async {
    await openDock(tester);
    await expectLater(find.byType(DockView), matchesGoldenFile('goldens/dock_rest.png'));
  });

  testWidgets('dock: hovering magnifies the icons under the cursor and shows the label', (tester) async {
    final (_, m) = await openDock(tester);
    final item = m.motion.items[4]; // Visual Studio Code
    await hoverAt(tester, m, item.x + item.size / 2);
    expect(m.mag.hovering, isTrue);
    expect(m.mag.zoom, 1);
    expect(m.hoverIndex, 4);
    expect(m.motion.items[4].size, closeTo(m.gp.iconSize * m.gp.maxScale, 1.5));
    expect(m.labelAlpha, closeTo(1, .01));
    await expectLater(find.byType(DockView), matchesGoldenFile('goldens/dock_hover.png'));
  });

  testWidgets('dock: the name label steps aside while a window preview is open over it', (tester) async {
    final (_, m) = await openDock(tester);
    final g = await tester.createGesture(kind: PointerDeviceKind.mouse);
    await g.addPointer(location: const Offset(1, 1));
    final it = m.motion.items[4];
    await g.moveTo(Offset(it.x + it.size / 2, m.metrics.plateTop + m.gp.dockHeight / 2));
    await settle(tester, 400);
    expect(m.labelAlpha, closeTo(1, .01));
    m.previewActive = true;
    m.requestFrame();
    await settle(tester, 400);
    expect(m.labelAlpha, 0, reason: 'the label would be behind the preview');
    m.previewActive = false;
    m.requestFrame();
    await settle(tester, 400);
    expect(m.labelAlpha, closeTo(1, .01), reason: 'and it is back once the preview is closed');
  });

  testWidgets('dock: leaving the window relaxes the zoom', (tester) async {
    final (_, m) = await openDock(tester);
    final g = await hoverAt(tester, m, m.motion.items[3].x + 20);
    expect(m.mag.zoom, 1);
    await g.moveTo(const Offset(-50, -50));
    await settle(tester, 900);
    expect(m.mag.hovering, isFalse);
    expect(m.mag.zoom, 0);
    expect(m.motion.items[3].size, m.gp.iconSize);
  });

  testWidgets('dock: Bar indicators, a custom colour and no magnification', (tester) async {
    await openDock(tester, settings: {'IndicatorStyle': 'Bar', 'BackgroundColor': '3A1F5C', 'Opacity': 90, 'MagnificationEnabled': false, 'CornerRadius': 30, 'IconSize': 40, 'DockHeight': 56});
    await expectLater(find.byType(DockView), matchesGoldenFile('goldens/dock_bar_purple.png'));
  });

  testWidgets('dock: on the left edge the same layout is turned to the side', (tester) async {
    final (_, m) = await openDock(tester, settings: {'Edge': 'Left'});
    expect(m.windowSize.width, lessThan(m.windowSize.height));
    final item = m.motion.items[3];
    await hoverAt(tester, m, item.x + item.size / 2);
    await expectLater(find.byType(DockView), matchesGoldenFile('goldens/dock_left_hover.png'));
  });

  testWidgets('dock: on the right edge', (tester) async {
    final (_, m) = await openDock(tester, settings: {'Edge': 'Right', 'IndicatorStyle': 'Bar'});
    expect(m.windowSize.width, lessThan(m.windowSize.height));
    await expectLater(find.byType(DockView), matchesGoldenFile('goldens/dock_right.png'));
  });

  testWidgets('dock: a click activates, a middle click opens a new instance, a right click opens the menu', (tester) async {
    final (_, m) = await openDock(tester);
    final activated = <String>[];
    DockEntry? menuFor;
    m.onActivate = (e, {bool newInstance = false}) => activated.add('${e.key}${newInstance ? '+new' : ''}');
    m.onContextMenu = (e, p) => menuFor = e;
    final item = m.motion.items[3];
    final point = m.axis.point(item.x + item.size / 2, m.metrics.plateTop + m.gp.dockHeight / 2);
    await tester.tapAt(point);
    await tester.pump(const Duration(milliseconds: 50));
    final middle = await tester.startGesture(point, kind: PointerDeviceKind.mouse, buttons: kMiddleMouseButton);
    await middle.up();
    await tester.pump(const Duration(milliseconds: 50));
    final right = await tester.startGesture(point, kind: PointerDeviceKind.mouse, buttons: kSecondaryMouseButton);
    await right.up();
    await tester.pump(const Duration(milliseconds: 50));
    expect(activated, ['app1', 'app1+new']);
    expect(menuFor?.key, 'app1');
  });

  testWidgets('dock: the Start button and the Launchpad button are ordinary clicks', (tester) async {
    final (_, m) = await openDock(tester);
    final clicked = <String>[];
    m.onActivate = (e, {bool newInstance = false}) => clicked.add(e.key);
    for (final i in [0, 1]) {
      final it = m.motion.items[i];
      await tester.tapAt(m.axis.point(it.x + it.size / 2, m.metrics.plateTop + m.gp.dockHeight / 2));
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(clicked, ['special:start', 'special:launchpad']);
  });

  testWidgets('dock: dragging a pinned app reorders it, dragging it away unpins it', (tester) async {
    final (_, m) = await openDock(tester);
    final moves = <(String, int)>[];
    final unpinned = <String>[];
    m.onMovePinned = (k, i) => moves.add((k, i));
    m.onUnpin = unpinned.add;
    final first = m.motion.items[2]; // File Explorer
    final start = m.axis.point(first.x + first.size / 2, m.metrics.plateTop + m.gp.dockHeight / 2);
    final g = await tester.startGesture(start, kind: PointerDeviceKind.mouse, buttons: kPrimaryButton);
    await tester.pump(const Duration(milliseconds: 50));
    await g.moveBy(const Offset(40, 0));
    await tester.pump(const Duration(milliseconds: 50));
    await g.moveBy(const Offset(120, 0));
    await tester.pump(const Duration(milliseconds: 50));
    expect(m.dragging, isTrue);
    expect(moves, isNotEmpty);
    expect(moves.first.$1, 'app0');
    expect(moves.first.$2, greaterThan(0));
    await g.moveBy(const Offset(0, -140));
    await tester.pump(const Duration(milliseconds: 50));
    await g.up();
    await tester.pump(const Duration(milliseconds: 50));
    expect(unpinned, ['app0']);
    expect(m.dragging, isFalse);
  });

  testWidgets('dock: a running, unpinned app cannot be dragged', (tester) async {
    final (_, m) = await openDock(tester);
    final moves = <String>[];
    m.onMovePinned = (k, i) => moves.add(k);
    final it = m.motion.items.last;
    final g = await tester.startGesture(m.axis.point(it.x + it.size / 2, m.metrics.plateTop + 30), kind: PointerDeviceKind.mouse, buttons: kPrimaryButton);
    await g.moveBy(const Offset(-80, 0));
    await tester.pump(const Duration(milliseconds: 50));
    expect(m.dragging, isFalse);
    expect(moves, isEmpty);
    await g.up();
  });

  testWidgets('dock: hovering a running app long enough asks for the window preview, leaving cancels it', (tester) async {
    final (_, m) = await openDock(tester, settings: {'PreviewDelayMs': 300});
    final previewed = <String>[];
    var hidden = 0;
    m.onPreview = (e, r) => previewed.add(e.key);
    m.onPreviewHide = () => hidden++;
    final item = m.motion.items[3];
    final g = await hoverAt(tester, m, item.x + item.size / 2);
    await settle(tester, 400);
    expect(previewed, ['app1']);
    await g.moveTo(const Offset(-40, -40));
    await tester.pump(const Duration(milliseconds: 100));
    expect(hidden, greaterThan(0));
  });

  testWidgets('dock: an app without windows gets no preview', (tester) async {
    final (_, m) = await openDock(tester, settings: {'PreviewDelayMs': 200});
    var asked = 0;
    m.onPreview = (e, r) => asked++;
    final item = m.motion.items[5]; // Telegram, not running
    await hoverAt(tester, m, item.x + item.size / 2);
    await tester.pump(const Duration(milliseconds: 400));
    expect(asked, 0);
  });

  testWidgets('dock: auto-hide slides the dock away after it has been idle and back when touched', (tester) async {
    final (_, m) = await openDock(tester, settings: {'AutoHide': true});
    await tester.pump(const Duration(milliseconds: 100));
    m.requestFrame();
    await tester.pump(const Duration(milliseconds: 100));
    await settle(tester, 2800);
    expect(m.hide, 1);
    expect(m.isHidden, isTrue);
    final item = m.motion.items[3];
    m.hide = 0.9;
    await hoverAt(tester, m, item.x + item.size / 2);
    expect(m.hide, lessThan(0.9), reason: 'it comes back while the pointer is on it');
  });

  testWidgets('dock: new apps grow in and the window keeps its size until the row outgrows it', (tester) async {
    final (_, m) = await openDock(tester);
    final before = m.windowSize;
    DockEntry extra(int i) => DockEntry(key: 'new$i', kind: DockEntryKind.app, name: 'Calc $i', running: true, app: {'Id': 'n$i', 'Name': 'Calc $i', 'Key': 'new$i'}, windows: [DockWindowInfo({'hwnd': 300 + i, 'title': 'Calc'})]);
    m.setEntries([...sampleEntries(), extra(1)]);
    await settle(tester, 48);
    expect(m.motion.items.last.appear, lessThan(1));
    await settle(tester, 500);
    expect(m.motion.items.last.appear, 1);
    expect(m.windowSize, before, reason: 'opening one app must not resize the native window (it would jump)');
    m.setEntries([...sampleEntries(), for (var i = 1; i <= 6; i++) extra(i)]);
    await settle(tester, 600);
    expect(m.windowSize.width, greaterThan(before.width), reason: 'the metrics grow once the row comes close to the window edge');
    final grown = m.windowSize;
    m.setEntries([...sampleEntries(), extra(1)]);
    await settle(tester, 600);
    expect(m.windowSize, grown, reason: 'closing a few apps does not shrink the window either');
  });

  testWidgets('dock: keyboard mode moves along the icons with the arrow keys and Enter activates', (tester) async {
    final (_, m) = await openDock(tester);
    final activated = <String>[];
    m.onActivate = (e, {bool newInstance = false}) => activated.add(e.key);
    m.enterKeyboardMode();
    await tester.pump(const Duration(milliseconds: 100));
    expect(m.kbIndex, 0);
    expect(m.handleKey(const _Key('ArrowRight')), isTrue);
    expect(m.kbIndex, 1);
    expect(m.handleKey(const _Key('ArrowRight')), isTrue);
    expect(m.kbIndex, 2);
    expect(m.handleKey(const _Key('Enter')), isTrue);
    expect(activated, ['app0']);
    expect(m.kbMode, isFalse);
  });
}

/// A stand-in for a key press that [DockModel.handleKey] understands in tests.
class _Key {
  const _Key(this.name);
  final String name;
}

extension on DockModel {
  bool handleKey(_Key k) {
    final logical = switch (k.name) {
      'ArrowRight' => LogicalKeyboardKeyAlias.right,
      'Enter' => LogicalKeyboardKeyAlias.enter,
      _ => LogicalKeyboardKeyAlias.escape,
    };
    return key(KeyDownEvent(physicalKey: PhysicalKeyboardKey.arrowRight, logicalKey: logical, timeStamp: Duration.zero)) == KeyEventResult.handled;
  }
}

class LogicalKeyboardKeyAlias {
  static final right = LogicalKeyboardKey.arrowRight;
  static final enter = LogicalKeyboardKey.enter;
  static final escape = LogicalKeyboardKey.escape;
}
