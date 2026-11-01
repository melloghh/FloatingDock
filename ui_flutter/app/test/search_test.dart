import 'package:fd_ui/ui/motion.dart';
import 'package:fd_ui/windows/search/search_window.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'helpers.dart';

/// The desktop behind a transparent window, so goldens show the margin and the glass-less surfaces as the user sees them.
Widget onDesktop(Widget child) => Directionality(textDirection: TextDirection.ltr, child: Stack(children: [
      Positioned.fill(
        child: DecoratedBox(
          decoration: const BoxDecoration(
            gradient: LinearGradient(colors: [Color(0xFF2B4A3C), Color(0xFF6B8F7B), Color(0xFF1F2F3A)], begin: Alignment.topLeft, end: Alignment.bottomRight),
          ),
        ),
      ),
      child,
    ]));

Map<String, Object?> app(String name) => {'Id': name, 'Name': name, 'ExePath': 'C:\\$name.exe', 'Key': 'exe:$name'};

Object? fakeSearch(Map<String, Object?>? params) {
  final q = '${params?['text']}'.toLowerCase();
  if (q.isEmpty) return {'results': <Object?>[], 'status': 'idle'};
  final names = ['Zen Browser', 'Zoom', 'Zed', 'Telegram'].where((n) => n.toLowerCase().contains(q)).toList();
  final results = <Map<String, Object?>>[
    if ('floatingdock settings'.contains(q)) {'id': 'cmd:settings', 'kind': 'Command', 'name': 'FloatingDock settings'},
    for (final n in names) {'id': 'app:exe:$n', 'kind': 'App', 'name': n, 'app': app(n)},
  ];
  return {'results': results, 'status': results.isEmpty ? 'none' : 'ok'};
}

Future<(Harness, SearchModel)> open(WidgetTester tester, {bool russian = false}) async {
  final h = await makeHarness(russian: russian);
  h.engine.handlers['search.query'] = fakeSearch;
  final model = SearchModel();
  await pumpWindow(tester, onDesktop(appFor(h.state, SearchView(model: model))), size: const Size(706, 700));
  model.reopen(width: 650, height: 580);
  model.visible.value = true;
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 600));
  return (h, model);
}

void main() {
  setUpAll(loadFonts);

  testWidgets('search: idle state shows the field and the hint', (tester) async {
    await open(tester);
    expect(find.text('Search apps'), findsWidgets);
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/search_idle.png'));
  });

  testWidgets('search: results with the glide highlight on the first row', (tester) async {
    final (h, _) = await open(tester);
    await tester.enterText(find.byType(TextField), 'z');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pump(const Duration(milliseconds: 600));
    expect(find.text('Zen Browser'), findsOneWidget);
    expect(h.engine.calls.where((c) => c == 'search.query').length, greaterThanOrEqualTo(2));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/search_results.png'));
  });

  testWidgets('search: Russian labels', (tester) async {
    await open(tester, russian: true);
    await tester.enterText(find.byType(TextField), 'floating');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pump(const Duration(milliseconds: 600));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/search_results_ru.png'));
  });

  testWidgets('search: arrows move the selection, Enter opens it, Esc closes', (tester) async {
    final (h, model) = await open(tester);
    var closed = 0;
    model.onClose = () => closed++;
    final opened = <String>[];
    h.engine.handlers['search.open'] = (p) {
      opened.add('${p?['id']}');
      return null;
    };
    await tester.enterText(find.byType(TextField), 'z');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pump(const Duration(milliseconds: 600));
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump(const Duration(milliseconds: 400));
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(opened, ['app:exe:Zoom']);
    expect(closed, 1, reason: 'opening a result closes the palette');
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    expect(closed, 2);
  });

  testWidgets('search: no matches says so', (tester) async {
    await open(tester);
    await tester.enterText(find.byType(TextField), 'qqq');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.text('No matches. Try an app name.'), findsOneWidget);
  });

  testWidgets('pop: overshoots to 1.04 and settles at 1, rises and fades in', (tester) async {
    final shown = ValueNotifier<bool>(false);
    await tester.pumpWidget(Directionality(
      textDirection: TextDirection.ltr,
      child: ValueListenableBuilder<bool>(
        valueListenable: shown,
        builder: (context, v, _) => PopSurface(visible: v, child: const SizedBox(width: 100, height: 100)),
      ),
    ));
    // The surface has a translate and a scale transform; the translate one has x-scale 1, so the product is the scale.
    double scale() => tester.widgetList<Transform>(find.byType(Transform)).fold<double>(1, (a, w) => a * w.transform.storage[0]);
    double opacity() => tester.widget<Opacity>(find.descendant(of: find.byType(PopSurface), matching: find.byType(Opacity)).first).opacity;
    expect(find.byType(Transform), findsNothing, reason: 'nothing is built while hidden');
    shown.value = true;
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 16));
    expect(scale(), inInclusiveRange(.84, .9));
    var peak = 0.0;
    for (var ms = 0; ms < 420; ms += 16) {
      await tester.pump(const Duration(milliseconds: 16));
      if (find.byType(Transform).evaluate().isNotEmpty) peak = peak > scale() ? peak : scale();
    }
    expect(peak, greaterThan(1.02), reason: 'the surface overshoots');
    expect(peak, lessThan(1.06));
    expect(scale(), closeTo(1.0, .001));
    expect(opacity(), closeTo(1.0, .001));
  });
}
