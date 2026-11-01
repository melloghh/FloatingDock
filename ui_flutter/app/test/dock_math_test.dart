import 'package:fd_ui/windows/dock/dock_math.dart';
import 'package:flutter_test/flutter_test.dart';

GeometryParams params({double icon = 48, double gap = 6, double height = 64, double scale = 1.7, double radius = 150, String align = 'Center', bool fixed = false}) =>
    GeometryParams(
      iconSize: icon, gap: gap, dockHeight: height, fixedWidth: fixed, fixedWidthValue: 900, alignment: align, maxScale: scale, radius: radius, labelHeadroom: 34,
    );

List<DockEntry> entries(int apps, {bool separator = false}) => [
      DockEntry(key: 'special:start', kind: DockEntryKind.start, name: 'Start'),
      for (var i = 0; i < apps; i++) DockEntry(key: 'app$i', kind: DockEntryKind.app, name: 'App $i', pinned: true, running: i.isEven, active: i == 2),
      if (separator) DockEntry(key: 'special:separator', kind: DockEntryKind.separator),
    ];

DockMotion settled(List<DockEntry> e) => DockMotion()..setEntries(e, animate: false);

void main() {
  group('Magnification maths', () {
    test('erf matches known values', () {
      expect(Magnification.erf(0), 0);
      expect(Magnification.erf(0.5), closeTo(0.5204998778, 1e-6));
      expect(Magnification.erf(1), closeTo(0.8427007929, 1e-6));
      expect(Magnification.erf(2), closeTo(0.9953222650, 1e-6));
      expect(Magnification.erf(-1), closeTo(-0.8427007929, 1e-6));
    });

    test('the bell is 1 at the centre, 0 at the radius and beyond, and falls monotonically', () {
      expect(Magnification.bell(0, 150), closeTo(1, 1e-12));
      expect(Magnification.bell(150, 150), 0);
      expect(Magnification.bell(400, 150), 0);
      var last = 1.0;
      for (var t = 5.0; t < 150; t += 5) {
        final v = Magnification.bell(t, 150);
        expect(v, lessThan(last));
        last = v;
      }
    });

    test('its integral is odd, increasing and constant beyond the radius', () {
      expect(Magnification.bellInt(0, 150), 0);
      expect(Magnification.bellInt(-30, 150), closeTo(-Magnification.bellInt(30, 150), 1e-9));
      expect(Magnification.bellInt(200, 150), closeTo(Magnification.bellInt(150, 150), 1e-9));
      expect(Magnification.bellInt(60, 150), greaterThan(Magnification.bellInt(30, 150)));
      // d/dt of the integral is the bell
      const h = 1e-3;
      expect((Magnification.bellInt(40 + h, 150) - Magnification.bellInt(40 - h, 150)) / (2 * h), closeTo(Magnification.bell(40, 150), 1e-4));
    });

    test('the envelope eases between linear and smootherstep', () {
      expect(Magnification.easeEnvelope(.5, 0), .5);
      expect(Magnification.easeEnvelope(.25, 5), closeTo(.25 * .25 * (3 - 2 * .25), 1e-12));
      expect(Magnification.easeEnvelope(0, 10), 0);
      expect(Magnification.easeEnvelope(1, 10), 1);
      expect(Magnification.easeEnvelope(.1, 10), lessThan(Magnification.easeEnvelope(.1, 0)));
    });

    test('hover zoom rises over the configured time and falls back to rest', () {
      final s = MagnificationState()..hovering = true;
      s.setCursor(100);
      var t = 0.0;
      while (s.advance(.016, responsiveness: 10, animationSpeed: 6, smoothness: 5) && t < 2) {
        t += .016;
      }
      expect(s.zoom, 1);
      expect(t, inInclusiveRange(.2, .45));
      s.hovering = false;
      for (var i = 0; i < 60; i++) {
        s.advance(.016, responsiveness: 10, animationSpeed: 6, smoothness: 5);
      }
      expect(s.zoom, 0);
      expect(s.isSettled, isTrue);
    });
  });

  group('Layout', () {
    test('at rest the row is symmetric about the window centre and nothing is magnified', () {
      final p = params();
      final motion = settled(entries(5));
      final m = WindowMetrics.compute(p, motion.items.length, 0);
      DockLayout.compute(p, m, motion.items, 0, 0);
      final first = motion.items.first, last = motion.items.last;
      expect(first.size, p.iconSize);
      expect((first.x - (m.windowWidth - (last.x + last.size))).abs(), lessThan(1e-6), reason: 'left and right margins match');
      expect(motion.items[1].x - (first.x + first.size), closeTo(p.gap, 1e-9));
    });

    test('the icon under the cursor grows to exactly the configured scale and the neighbours less', () {
      final p = params(scale: 1.7);
      final motion = settled(entries(7));
      final m = WindowMetrics.compute(p, motion.items.length, 0);
      DockLayout.compute(p, m, motion.items, 0, 0);
      final center = motion.items[4].x + p.iconSize / 2;
      DockLayout.compute(p, m, motion.items, center, 1);
      expect(motion.items[4].size, closeTo(p.iconSize * 1.7, 1.0));
      expect(motion.items[3].size, lessThan(motion.items[4].size));
      expect(motion.items[3].size, greaterThan(p.iconSize));
      expect(motion.items[0].size, lessThan(motion.items[3].size));
      // everything stays in order and never overlaps
      for (var i = 1; i < motion.items.length; i++) {
        expect(motion.items[i].x, greaterThanOrEqualTo(motion.items[i - 1].x + motion.items[i - 1].size - 1e-6));
      }
    });

    test('zoom 0 and zoom 1 differ only by the magnification, never by a jump between two cursor positions', () {
      final p = params();
      final motion = settled(entries(7));
      final m = WindowMetrics.compute(p, motion.items.length, 0);
      DockLayout.compute(p, m, motion.items, 100, 1);
      final a = [for (final i in motion.items) i.x];
      DockLayout.compute(p, m, motion.items, 100.5, 1);
      final b = [for (final i in motion.items) i.x];
      for (var i = 0; i < a.length; i++) {
        expect((a[i] - b[i]).abs(), lessThan(1.0), reason: 'item $i moves smoothly with the cursor');
      }
    });

    test('the plate always spans the items plus padding', () {
      final p = params();
      final motion = settled(entries(4));
      final m = WindowMetrics.compute(p, motion.items.length, 0);
      final res = DockLayout.compute(p, m, motion.items, 0, 0);
      expect(res.plateLeft, closeTo(motion.items.first.x - p.padX, 1e-9));
      expect(res.plateRight, closeTo(motion.items.last.x + motion.items.last.size + p.padX, 1e-9));
      expect(res.plateRight - res.plateLeft, closeTo(WindowMetrics.span(p, [for (final _ in motion.items) (1.0, false)]) + 2 * p.padX, 1e-6));
    });

    test('left and right alignment hug the window edge', () {
      final motion = settled(entries(4));
      final left = params(align: 'Left');
      final ml = WindowMetrics.compute(left, 5, 0);
      final rl = DockLayout.compute(left, ml, motion.items, 0, 0);
      expect(rl.plateLeft, closeTo(ml.shadowMargin + ml.overshoot, 1e-9));
      final right = params(align: 'Right');
      final mr = WindowMetrics.compute(right, 5, 0);
      final rr = DockLayout.compute(right, mr, motion.items, 0, 0);
      expect(rr.plateRight, closeTo(mr.windowWidth - mr.shadowMargin - mr.overshoot, 1.0));
    });

    test('a fixed width plate is at least that wide and centres the row', () {
      final p = params(fixed: true);
      final motion = settled(entries(3));
      final m = WindowMetrics.compute(p, 4, 0);
      final res = DockLayout.compute(p, m, motion.items, 0, 0);
      expect(res.plateRight - res.plateLeft, greaterThanOrEqualTo(900));
    });

    test('the window leaves headroom for the grown icons and the label', () {
      final p = params();
      final m = WindowMetrics.compute(p, 6, 0);
      final topOfBiggest = m.plateBottom - p.iconBottomInset - p.iconSize * p.maxScale;
      expect(topOfBiggest, greaterThan(0), reason: 'the magnified icon still fits inside the window');
      expect(m.keepZone.contains(m.enterZone.center), isTrue);
      expect(m.enterZone.top, m.plateTop);
    });

    test('without magnification the window is only as tall as the dock plus shadow', () {
      final big = WindowMetrics.compute(params(), 6, 0);
      final flat = WindowMetrics.compute(params(scale: 1), 6, 0);
      expect(flat.windowHeight, lessThan(big.windowHeight));
      expect(flat.overshoot, 2);
    });
  });

  group('Hit testing', () {
    test('gaps belong to the nearest icon and separators are never hit', () {
      final p = params();
      final motion = settled(entries(4, separator: true)..insert(3, DockEntry(key: 'sep', kind: DockEntryKind.separator)));
      final m = WindowMetrics.compute(p, motion.items.length, 1);
      DockLayout.compute(p, m, motion.items, 0, 0);
      final first = motion.items[0];
      final y = m.plateTop + 20;
      expect(DockLayout.hitTest(motion.items, p, m, first.x + 5, y), 0);
      expect(DockLayout.hitTest(motion.items, p, m, first.x + first.size + p.gap * .4, y), 0, reason: 'half a gap still belongs to the icon');
      final sep = motion.items[3];
      expect(DockLayout.hitTest(motion.items, p, m, sep.x + sep.size / 2, y), isNot(3));
      expect(DockLayout.hitTest(motion.items, p, m, first.x + 5, m.plateTop - 200), -1, reason: 'far above the dock');
      expect(DockLayout.hitTest(motion.items, p, m, first.x + 5, m.plateBottom + 10), -1, reason: 'below the plate');
    });
  });

  group('Motion', () {
    test('a new entry grows in, a removed one fades out and is then dropped', () {
      final motion = settled(entries(3));
      final more = entries(3)..add(DockEntry(key: 'new', kind: DockEntryKind.app, name: 'New', running: true));
      motion.setEntries(more, animate: true);
      final added = motion.items.last;
      expect(added.appear, 0);
      var steps = 0;
      while (motion.advance(.016, iconAnimation: true, iconAnimationMs: 240) && steps < 100) {
        steps++;
      }
      expect(added.appear, 1);
      expect(steps * .016, inInclusiveRange(.2, .3));

      motion.setEntries(entries(3), animate: true);
      expect(motion.items.last.leaving, isTrue);
      expect(motion.liveCount, 4);
      while (motion.advance(.016, iconAnimation: true, iconAnimationMs: 240)) {}
      expect(motion.items.length, 4);
      expect(motion.items.any((i) => i.key == 'new'), isFalse);
    });

    test('a leaving item keeps its place between its neighbours', () {
      final motion = settled(entries(4));
      final without = entries(4)..removeAt(2); // app1 is gone
      motion.setEntries(without, animate: true);
      final keys = motion.items.map((i) => i.key).toList();
      expect(keys.indexOf('app1'), greaterThan(keys.indexOf('app0')));
      expect(keys.indexOf('app1'), lessThan(keys.indexOf('app2')));
    });

    test('reordering makes the moved items slide from where they were', () {
      final p = params();
      final motion = settled(entries(3));
      final m = WindowMetrics.compute(p, 4, 0);
      DockLayout.compute(p, m, motion.items, 0, 0);
      final before = {for (final i in motion.items) i.key: i.x};
      final reordered = entries(3);
      final moved = reordered.removeAt(1);
      reordered.add(moved);
      motion.setEntries(reordered, animate: true);
      DockLayout.compute(p, m, motion.items, 0, 0);
      for (final i in motion.items.where((i) => i.key == 'app0')) {
        expect(i.x, closeTo(before['app0']!, 1e-6), reason: 'the first app did not move');
      }
      final slid = motion.items.firstWhere((i) => i.key == 'app2');
      expect(slid.slide, isNot(0));
      expect(slid.x, closeTo(before['app2']!, 1e-6), reason: 'it starts where it was');
      while (motion.advance(.016, iconAnimation: true, iconAnimationMs: 240)) {
        DockLayout.compute(p, m, motion.items, 0, 0);
      }
      expect(slid.slide, 0);
    });

    test('no animation: everything is there at once', () {
      final motion = DockMotion()..setEntries(entries(3), animate: false);
      expect(motion.items.every((i) => i.appear == 1 && i.presence == 1), isTrue);
    });
  });
}
