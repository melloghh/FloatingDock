import 'package:fd_ui/windows/dock/dock_math.dart';
import 'package:fd_ui/windows/dock/preview_view.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'helpers.dart';

Future<(Harness, PreviewModel)> openPreview(WidgetTester tester, {int windows = 3}) async {
  final h = await makeHarness();
  final model = PreviewModel();
  final cards = [
    for (var i = 0; i < windows; i++)
      PreviewLayout(DockWindowInfo({'hwnd': 100 + i, 'title': ['Documentation — Zen Browser', 'Inbox', 'A very long window title that has to be cut with an ellipsis'][i % 3]}), thumbSizeFor(Size(1600 - i * 400, 900))),
  ];
  final panel = PreviewModel.panelSize(windows, 150);
  await pumpWindow(tester, Directionality(textDirection: TextDirection.ltr, child: Stack(children: [
    const Positioned.fill(child: ColoredBox(color: Color(0xFF3A5F4B))),
    appFor(h.state, PreviewView(model: model)),
  ])), size: Size(panel.width + 56, panel.height + 56));
  model.open(appName: 'Zen Browser', app: {'Id': 'a', 'Name': 'Zen Browser', 'ExePath': 'C:\\zen.exe'}, cards: cards);
  model.visible.value = true;
  await tester.pump();
  for (var t = 0; t < 600; t += 16) {
    await tester.pump(const Duration(milliseconds: 16));
  }
  return (h, model);
}

void main() {
  setUpAll(loadFonts);

  test('pictures keep the aspect ratio of the window and never exceed the card', () {
    final wide = thumbSizeFor(const Size(1920, 1080));
    expect(wide.width / wide.height, closeTo(1920 / 1080, .02));
    expect(wide.width, lessThanOrEqualTo(previewCardWidth - 16));
    final tall = thumbSizeFor(const Size(600, 1200));
    expect(tall.height, lessThanOrEqualTo(previewThumbMax));
    expect(tall.width / tall.height, closeTo(.5, .03));
    expect(thumbSizeFor(null).width, greaterThan(100));
  });

  testWidgets('preview: one card per window, with title and close button', (tester) async {
    await openPreview(tester);
    expect(find.text('Inbox'), findsOneWidget);
    expect(find.byIcon(Icons.close_rounded), findsNWidgets(3));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/preview.png'));
  });

  testWidgets('preview: the live pictures are placed after the pop-in with each card\'s rectangle', (tester) async {
    final h = await makeHarness();
    final model = PreviewModel();
    List<(DockWindowInfo, Rect)>? rects;
    model.onThumbnails = (r) => rects = r;
    await pumpWindow(tester, appFor(h.state, PreviewView(model: model)), size: const Size(900, 300));
    model.open(appName: 'x', app: null, cards: [PreviewLayout(DockWindowInfo({'hwnd': 7, 'title': 'One'}), const Size(200, 120)), PreviewLayout(DockWindowInfo({'hwnd': 8, 'title': 'Two'}), const Size(150, 120))]);
    model.visible.value = true;
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 200));
    expect(rects, isNull, reason: 'not while the surface is still scaling');
    await tester.pump(const Duration(milliseconds: 400));
    expect(rects, hasLength(2));
    expect(rects![0].$2.width, closeTo(200, .01));
    expect(rects![0].$2.height, closeTo(120, .01));
    expect(rects![1].$2.left, greaterThan(rects![0].$2.right), reason: 'the second card is to the right');
  });

  testWidgets('preview: a card activates its window, the X closes it, the pointer is reported', (tester) async {
    final (_, m) = await openPreview(tester);
    final activated = <int>[], closed = <int>[];
    final pointer = <bool>[];
    m.onActivate = (w) => activated.add(w.hwnd);
    m.onClose = (w) => closed.add(w.hwnd);
    m.onPointer = pointer.add;
    await tester.tap(find.text('Inbox'));
    await tester.tap(find.byIcon(Icons.close_rounded).first);
    await tester.pump();
    expect(activated, [101]);
    expect(closed, [100]);
    final g = await tester.createGesture(kind: PointerDeviceKind.mouse);
    await g.addPointer(location: const Offset(1, 1));
    await g.moveTo(tester.getCenter(find.text('Inbox')));
    await g.moveTo(const Offset(1, 1));
    expect(pointer, containsAllInOrder([true, false]));
  });
}
