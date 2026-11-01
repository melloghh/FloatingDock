import 'package:fd_ui/windows/bar/bar_view.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'helpers.dart';

Widget onDesktop(Widget child) => Directionality(
      textDirection: TextDirection.ltr,
      child: Stack(children: [
        const Positioned.fill(
          child: DecoratedBox(
            decoration: BoxDecoration(
              gradient: LinearGradient(colors: [Color(0xFF3A5F4B), Color(0xFF9DB7A6), Color(0xFF2F4858)], begin: Alignment.centerLeft, end: Alignment.centerRight),
            ),
          ),
        ),
        child,
      ]),
    );

Future<(Harness, BarModel)> openBar(WidgetTester tester,
    {Map<String, Object?> settings = const {}, bool media = true, bool russian = false, double width = 1280}) async {
  final h = await makeHarness(settings: settings, russian: russian);
  h.state.foregroundName = 'Visual Studio Code';
  h.engine.responses.addAll({
    'media.get': media ? {'title': 'Midnight City', 'artist': 'M83', 'playing': true, 'canToggle': true, 'canNext': true} : null,
    'battery.get': {'present': true, 'percent': 87, 'charging': true},
    'language.current': {'tag': 'EN'},
    'network.available': true,
  });
  final model = BarModel('0,0')
    ..origin = const Offset(0, 0)
    ..scale = 1.5
    ..widthDip = width;
  await pumpWindow(tester, onDesktop(appFor(h.state, BarView(model: model, now: () => DateTime(2026, 11, 1, 20, 24, 36)))), size: Size(width, 52));
  await tester.pump(const Duration(milliseconds: 100));
  return (h, model);
}

void main() {
  setUpAll(loadFonts);

  testWidgets('bar: islands on the glass strip', (tester) async {
    await openBar(tester);
    expect(find.text('Visual Studio Code'), findsOneWidget);
    expect(find.text('Midnight City'), findsOneWidget);
    expect(find.text('87%'), findsNothing, reason: 'charging shows an arrow first') ;
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/bar.png'));
  });

  testWidgets('bar: with the date, seconds and Russian', (tester) async {
    await openBar(tester, settings: {'TopBarShowDate': true, 'TopBarShowSeconds': true}, media: false, russian: true);
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/bar_ru_date.png'));
  });

  testWidgets('bar: hidden items are really gone', (tester) async {
    await openBar(tester, settings: {'TopBarMediaEnabled': false, 'TopBarTrayEnabled': false, 'TopBarSearchEnabled': false}, media: true);
    expect(find.byIcon(Icons.search_rounded), findsNothing);
    expect(find.byIcon(Icons.music_note_rounded), findsNothing);
    expect(find.byIcon(Icons.expand_more_rounded), findsNothing);
    expect(find.text('Midnight City'), findsNothing);
    expect(find.byIcon(Icons.volume_up_rounded), findsOneWidget);
  });

  testWidgets('bar: a button hands its screen rectangle to the controller', (tester) async {
    final (_, model) = await openBar(tester);
    String? kind;
    Rect? anchor;
    model.onMenu = (k, a) {
      kind = k;
      anchor = a;
    };
    final local = tester.getRect(find.byIcon(Icons.volume_up_rounded));
    await tester.tap(find.byIcon(Icons.volume_up_rounded));
    await tester.pump();
    expect(kind, 'sound');
    // Origin (0, 0) at scale 1.5: the rectangle is the button's own, in physical pixels, and sits inside the button.
    expect(anchor!.center.dx, closeTo(local.center.dx * 1.5, 14));
    expect(anchor!.top, lessThan(40 * 1.5));
  });

  testWidgets('bar: search, settings gear and the player buttons call out', (tester) async {
    final (h, model) = await openBar(tester);
    var searched = 0;
    final uris = <String>[];
    model.onSearch = () => searched++;
    model.onOpenUri = uris.add;
    final controls = <Object?>[];
    h.engine.handlers['media.control'] = (p) {
      controls.add(p?['command']);
      return {'ok': true};
    };
    await tester.tap(find.byIcon(Icons.search_rounded));
    await tester.tap(find.byIcon(Icons.settings_rounded));
    await tester.tap(find.byIcon(Icons.skip_next_rounded));
    await tester.tap(find.byIcon(Icons.pause_rounded));
    await tester.pump();
    expect(searched, 1);
    expect(uris, ['ms-settings:']);
    expect(controls, ['next', 'toggle']);
  });

  testWidgets('bar: sliding away moves the strip off the screen and back', (tester) async {
    final (_, model) = await openBar(tester);
    double top() => tester.getRect(find.text('Visual Studio Code')).top;
    final shown = top();
    model.hidden.value = true;
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(top(), lessThan(shown - 30), reason: 'moved up by about the bar height');
    model.hidden.value = false;
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(top(), closeTo(shown, .5));
  });

  testWidgets('bar: language and network events update the island', (tester) async {
    final (h, _) = await openBar(tester);
    await tester.pump(const Duration(milliseconds: 200));
    expect(find.text('EN'), findsOneWidget);
    h.engine.emit('language.changed', {'tag': 'RU'});
    await tester.pump(const Duration(milliseconds: 50));
    expect(find.text('RU'), findsOneWidget);
    h.engine.emit('network.changed', {'available': false});
    await tester.pump(const Duration(milliseconds: 50));
    expect(tester.widget<Opacity>(find.ancestor(of: find.byIcon(Icons.wifi_rounded), matching: find.byType(Opacity)).first).opacity, closeTo(.4, .001));
  });
}