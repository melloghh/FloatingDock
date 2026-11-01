import 'package:fd_ui/windows/settings/pages.dart';
import 'package:fd_ui/windows/settings/settings_window.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'helpers.dart';

void main() {
  setUpAll(loadFonts);

  for (final page in settingsPages) {
    testWidgets('settings: ${page.name} (dark)', (tester) async {
      final h = await makeHarness();
      await pumpWindow(tester, appFor(h.state, SettingsView(initialPage: page.name)));
      await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/settings_${page.name.replaceAll(' ', '_').toLowerCase()}_dark.png'));
    });
  }

  testWidgets('settings: Dock (light, mint)', (tester) async {
    final h = await makeHarness(settings: {'Theme': 'Light'});
    await pumpWindow(tester, appFor(h.state, const SettingsView(initialPage: 'Dock')));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/settings_dock_light.png'));
  });

  testWidgets('settings: Appearance (light, graphite)', (tester) async {
    final h = await makeHarness(settings: {'Theme': 'Light', 'AccentSeed': '5F6368'});
    await pumpWindow(tester, appFor(h.state, const SettingsView(initialPage: 'Appearance')));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/settings_appearance_light_graphite.png'));
  });

  testWidgets('settings: Behaviour in Russian', (tester) async {
    final h = await makeHarness(russian: true);
    await pumpWindow(tester, appFor(h.state, const SettingsView(initialPage: 'Behaviour')));
    await expectLater(find.byType(MaterialApp), matchesGoldenFile('goldens/settings_behaviour_ru.png'));
  });

  testWidgets('a toggle changes the setting locally and is sent to the engine', (tester) async {
    final h = await makeHarness();
    await pumpWindow(tester, appFor(h.state, const SettingsView(initialPage: 'Behaviour')));
    expect(h.state.setting<bool>('AutoHide', true), isFalse);
    await tester.tap(find.text('Auto-hide the dock'));
    await tester.pump();
    expect(h.state.setting<bool>('AutoHide', false), isTrue, reason: 'applied at once');
    await tester.pump(const Duration(milliseconds: 200));
    expect(h.engine.settings['AutoHide'], isTrue, reason: 'sent to the engine');
  });

  testWidgets('dragging a slider coalesces into few engine calls', (tester) async {
    final h = await makeHarness();
    await pumpWindow(tester, appFor(h.state, const SettingsView(initialPage: 'Dock')));
    final slider = find.byType(Slider).first;
    await tester.drag(slider, const Offset(120, 0));
    await tester.pump(const Duration(milliseconds: 300));
    final sets = h.engine.calls.where((c) => c == 'settings.set').length;
    expect(sets, inInclusiveRange(1, 6));
    expect(h.engine.settings['IconSize'], isNot(48));
  });

  testWidgets('an invalid color is shown as an error and not sent', (tester) async {
    final h = await makeHarness();
    await pumpWindow(tester, appFor(h.state, const SettingsView(initialPage: 'Appearance')));
    final field = find.widgetWithText(TextField, '1E1E1E');
    await tester.enterText(field, 'zzz');
    await tester.pump();
    expect(find.text('Use 6 hex digits, e.g. 1E1E1E'), findsOneWidget);
    expect(h.engine.settings['BackgroundColor'], '1E1E1E');
  });

  testWidgets('switching pages glides the highlight and replays the reveal', (tester) async {
    final h = await makeHarness();
    await pumpWindow(tester, appFor(h.state, const SettingsView()));
    await tester.tap(find.text('Magnification'));
    await tester.pump(const Duration(milliseconds: 120));
    expect(find.text('Magnify icons under the cursor'), findsOneWidget);
    await tester.pump(const Duration(milliseconds: 1000));
  });
}
