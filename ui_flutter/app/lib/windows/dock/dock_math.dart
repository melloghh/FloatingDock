import 'dart:math' as math;

import 'package:flutter/painting.dart';

/// Port of FloatingDock.Core.Geometry (Magnification, DockGeometry, DockMotion, DockLayout): the maths of the
/// "fisheye" dock. Pure Dart, no widgets, so it is unit-tested on its own.

// ---------------------------------------------------------------------- entries

enum DockEntryKind { start, launchpad, app, separator }

class DockWindowInfo {
  DockWindowInfo(Map m)
      : hwnd = (m['hwnd'] as num).toInt(),
        title = '${m['title'] ?? ''}',
        minimized = m['minimized'] == true;
  final int hwnd;
  final String title;
  final bool minimized;
}

/// One slot of the dock as the engine reports it.
class DockEntry {
  DockEntry({required this.key, required this.kind, this.name = '', this.pinned = false, this.running = false, this.active = false, this.app, this.windows = const []});

  factory DockEntry.fromJson(Map m) => DockEntry(
        key: '${m['key']}',
        kind: switch ('${m['kind']}') {
          'Start' => DockEntryKind.start,
          'Launchpad' => DockEntryKind.launchpad,
          'Separator' => DockEntryKind.separator,
          _ => DockEntryKind.app,
        },
        name: '${m['name'] ?? ''}',
        pinned: m['pinned'] == true,
        running: m['running'] == true,
        active: m['active'] == true,
        app: (m['app'] as Map?)?.cast<String, Object?>(),
        windows: [for (final w in (m['windows'] as List? ?? const []).cast<Map>()) DockWindowInfo(w)],
      );

  final String key, name;
  final DockEntryKind kind;
  final bool pinned, running, active;
  final Map<String, Object?>? app;
  final List<DockWindowInfo> windows;
}

// ---------------------------------------------------------------------- magnification

abstract final class Magnification {
  static final double _e4 = math.exp(-4);
  static final double _erfTwo = erf(2);

  /// 0..1 bell: (exp(-4 (t/R)^2) - e^-4) / (1 - e^-4) for |t| < R, 0 beyond.
  static double bell(double t, double radius) {
    final a = t.abs();
    if (a >= radius) return 0;
    final r = a / radius;
    return (math.exp(-4 * r * r) - _e4) / (1 - _e4);
  }

  /// Integral of [bell] from 0 to t (odd, constant beyond +-radius).
  static double bellInt(double t, double radius) {
    final a = math.min(t.abs(), radius);
    final e = a == radius ? _erfTwo : erf(2 * a / radius);
    final v = (radius * 0.44311346 * e - _e4 * a) / (1 - _e4);
    return t < 0 ? -v : v;
  }

  /// Error function (Numerical Recipes erfc Chebyshev fit), |error| < 1.2e-7.
  static double erf(double x) {
    final z = x.abs();
    if (z < 0.5) {
      final z2 = z * z;
      final series = z *
          (1.0 - z2 / 3.0 * (1.0 - z2 * 3.0 / 10.0 * (1.0 - z2 * 5.0 / 21.0 * (1.0 - z2 * 7.0 / 36.0 * (1.0 - z2 * 9.0 / 55.0))))) *
          1.1283791670955126;
      return x >= 0 ? series : -series;
    }
    final t = 1.0 / (1.0 + 0.5 * z);
    final ans = t *
        math.exp(-z * z -
            1.26551223 +
            t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
    final e = 1.0 - ans;
    return x >= 0 ? e : -e;
  }

  static double smooth01(double t) {
    t = t.clamp(0.0, 1.0);
    return t * t * (3 - 2 * t);
  }

  /// Easing of the zoom envelope: smoothness 0 = linear, 5 = smoothstep, 10 = smootherstep.
  static double easeEnvelope(double t, int smoothness) {
    t = t.clamp(0.0, 1.0);
    final lin = t;
    final s3 = t * t * (3 - 2 * t);
    final s5 = t * t * t * (t * (t * 6 - 15) + 10);
    final k = smoothness.clamp(0, 10) / 10;
    return k <= 0.5 ? lin + (s3 - lin) * (k / 0.5) : s3 + (s5 - s3) * ((k - 0.5) / 0.5);
  }

  /// Strength factor so that the icon directly under the cursor reaches exactly the configured scale.
  static double normalisation(double iconSize, double radius) {
    final mean = 2 * bellInt(iconSize * 0.5, radius) / iconSize;
    return mean > 0.05 ? math.min(1 / mean, 1.5) : 1.5;
  }
}

/// Hover / zoom state: cursor smoothing and the eased zoom envelope. Advances on real elapsed time.
class MagnificationState {
  bool hovering = false;
  double zoom = 0, cursorX = 0;
  double _linear = 0, _target = 0;
  bool _hasCursor = false;

  void reset() {
    hovering = false;
    _linear = 0;
    zoom = 0;
    _hasCursor = false;
  }

  void setCursor(double x, {bool snap = false}) {
    if (!_hasCursor || snap) {
      cursorX = x;
      _hasCursor = true;
    }
    _target = x;
  }

  bool get isSettled => !hovering && _linear <= 0;

  /// True while something is still moving (the caller keeps its frame loop alive).
  bool advance(double dt, {required int responsiveness, required int animationSpeed, required int smoothness}) {
    dt = dt.clamp(0.0, 0.1);
    final seconds = 0.45 - (animationSpeed.clamp(1, 10) - 1) * (0.36 / 9);
    final step = dt / seconds;
    _linear = hovering ? math.min(1, _linear + step) : math.max(0, _linear - step);
    zoom = Magnification.easeEnvelope(_linear, smoothness);
    var moving = false;
    if (hovering) {
      if (responsiveness >= 10) {
        moving = cursorX != _target;
        cursorX = _target;
      } else {
        final rate = 6 + (responsiveness - 1) * (54 / 9);
        final k = 1 - math.exp(-rate * dt);
        cursorX += (_target - cursorX) * k;
        if ((_target - cursorX).abs() < 0.05) {
          cursorX = _target;
        } else {
          moving = true;
        }
      }
    }
    return (hovering ? _linear < 1 : _linear > 0) || moving;
  }
}

// ---------------------------------------------------------------------- geometry

/// Numeric inputs of the layout (DIP), derived from the settings.
class GeometryParams {
  const GeometryParams({
    required this.iconSize,
    required this.gap,
    required this.dockHeight,
    required this.fixedWidth,
    required this.fixedWidthValue,
    required this.alignment,
    required this.maxScale,
    required this.radius,
    this.separatorWidth = 10,
    this.shadowMargin = 28,
    required this.labelHeadroom,
  });

  factory GeometryParams.fromSettings(Map<String, Object?> s) {
    double d(String k, double f) => (s[k] as num?)?.toDouble() ?? f;
    final magnify = s['MagnificationEnabled'] != false;
    return GeometryParams(
      iconSize: d('IconSize', 48),
      gap: d('IconSpacing', 6),
      dockHeight: d('DockHeight', 64),
      fixedWidth: s['WidthMode'] == 'Fixed',
      fixedWidthValue: d('FixedWidth', 900),
      alignment: '${s['Alignment'] ?? 'Center'}',
      maxScale: magnify ? d('MaxScalePercent', 170) / 100 : 1,
      radius: d('MagnificationRadius', 150),
      labelHeadroom: s['ShowLabels'] != false ? 34 : 0,
    );
  }

  final double iconSize, gap, dockHeight, fixedWidthValue, maxScale, radius, separatorWidth, shadowMargin, labelHeadroom;
  final bool fixedWidth;
  final String alignment; // Center | Left | Right

  double get vPad => (dockHeight - iconSize) * 0.5;
  double get padX => vPad;

  /// Room reserved above a 5 px indicator including its 1.25 active scale.
  double get iconBottomInset => math.max(vPad, 12);
}

/// Static window geometry: computed only from the item set and the settings, never from the cursor or the zoom.
class WindowMetrics {
  WindowMetrics({
    required this.windowWidth,
    required this.windowHeight,
    required this.restPlateLeft,
    required this.restPlateRight,
    required this.plateTop,
    required this.plateBottom,
    required this.overshoot,
    required this.shadowMargin,
    required this.enterZone,
    required this.keepZone,
  });

  final double windowWidth, windowHeight, restPlateLeft, restPlateRight, plateTop, plateBottom, overshoot, shadowMargin;
  final Rect enterZone, keepZone;

  static double span(GeometryParams p, Iterable<(double presence, bool separator)> items) {
    var span = 0.0, maxPresence = 0.0;
    for (final (pe, sep) in items) {
      final w = sep ? p.separatorWidth : p.iconSize;
      span += (w + p.gap) * pe;
      maxPresence = math.max(maxPresence, pe);
    }
    return math.max(0, span - maxPresence * p.gap);
  }

  /// [itemCount] includes separators and items still fading out, taken at presence 1.
  static WindowMetrics compute(GeometryParams p, int itemCount, int separatorCount, {double? viewportWidth}) {
    final list = [for (var i = 0; i < itemCount; i++) (1.0, i < separatorCount)];
    final sp = span(p, list);
    final plateW = p.fixedWidth ? math.max(p.fixedWidthValue, sp + 2 * p.padX) : sp + 2 * p.padX;
    final a = (p.maxScale - 1) * Magnification.normalisation(p.iconSize, p.radius);
    var overshoot = (a * Magnification.bellInt(p.radius, p.radius)).ceilToDouble() + 2;
    if (p.maxScale <= 1.0001) overshoot = 2;
    final winW = viewportWidth != null ? math.max(1.0, viewportWidth) : plateW + 2 * (overshoot + p.shadowMargin);
    final headroom = math.max(0.0, p.iconSize * p.maxScale - (p.dockHeight - p.iconBottomInset)) + p.labelHeadroom + 6;
    final winH = p.dockHeight + headroom + p.shadowMargin * 2;
    final plateB = winH - p.shadowMargin;
    final plateT = plateB - p.dockHeight;
    final restL = switch (p.alignment) {
      'Left' when !p.fixedWidth => p.shadowMargin + overshoot,
      'Right' when !p.fixedWidth => winW - p.shadowMargin - overshoot - plateW,
      _ => (winW - plateW) * 0.5,
    };
    final enter = Rect.fromLTWH(restL - 4, plateT, plateW + 8, p.dockHeight + p.shadowMargin);
    final hoverTop = math.max(0.0, plateB - p.iconBottomInset - p.iconSize * p.maxScale - 8);
    final keep = Rect.fromLTWH(restL - 12 - overshoot * 0.5, hoverTop, plateW + 24 + overshoot, winH - hoverTop);
    return WindowMetrics(
      windowWidth: winW.ceilToDouble(),
      windowHeight: winH.ceilToDouble(),
      restPlateLeft: restL,
      restPlateRight: restL + plateW,
      plateTop: plateT,
      plateBottom: plateB,
      overshoot: overshoot,
      shadowMargin: p.shadowMargin,
      enterZone: enter,
      keepZone: keep,
    );
  }
}

// ---------------------------------------------------------------------- motion

/// Visual state of one dock slot (including slots that are fading out).
class DockItem {
  DockItem(this.key, this.entry);
  final String key;
  DockEntry entry;
  bool leaving = false;
  double appear = 0, runningAlpha = 0, activeAlpha = 0, slide = 0, lastBaseX = 0;
  bool hasLastBase = false, pendingSlide = false;
  bool get isSeparator => entry.kind == DockEntryKind.separator;
  double get presence => Magnification.smooth01(appear);

  // outputs of the latest layout
  double baseX = 0, x = 0, size = 0, bottom = 0;
  double get top => bottom - size;
  double? dragX;
}

/// Owns the ordered list of items and advances appear / disappear / slide animations on elapsed time.
class DockMotion {
  final List<DockItem> items = [];

  int get liveCount => items.where((i) => !i.leaving).length;

  (int count, int separators) metricsShape() => (items.length, items.where((i) => i.isSeparator).length);

  void setEntries(List<DockEntry> entries, {required bool animate}) {
    final old = List<DockItem>.of(items);
    final oldByKey = {for (final i in old) i.key: i};
    final newKeys = {for (final e in entries) e.key};

    final ghostsAfter = <String, List<DockItem>>{};
    final leading = <DockItem>[];
    String? lastSurvivor;
    for (final o in old) {
      if (newKeys.contains(o.key)) {
        lastSurvivor = o.key;
        continue;
      }
      if (!animate || o.presence <= 0.001) continue; // nothing visible to animate out
      o.leaving = true;
      if (lastSurvivor == null) {
        leading.add(o);
      } else {
        (ghostsAfter[lastSurvivor] ??= []).add(o);
      }
    }

    final sameSet = old.where((o) => !o.leaving).length == entries.length && entries.every((e) => oldByKey[e.key] != null && !oldByKey[e.key]!.leaving);
    var orderChanged = false;
    if (sameSet) {
      final oldOrder = [for (final o in old.where((o) => !o.leaving)) o.key];
      for (var i = 0; i < oldOrder.length; i++) {
        if (oldOrder[i] != entries[i].key) {
          orderChanged = true;
          break;
        }
      }
    }

    final result = <DockItem>[...leading];
    for (final e in entries) {
      var item = oldByKey[e.key];
      if (item == null) {
        item = DockItem(e.key, e)
          ..appear = animate ? 0 : 1
          ..runningAlpha = e.running ? (animate ? 0 : 1) : 0
          ..activeAlpha = e.active ? 1 : 0;
      } else {
        item.entry = e;
        item.leaving = false;
        if (orderChanged && animate) item.pendingSlide = true;
      }
      result.add(item);
      final g = ghostsAfter[e.key];
      if (g != null) result.addAll(g);
    }
    items
      ..clear()
      ..addAll(result);
    if (!animate) {
      for (final i in items) {
        i.appear = 1;
      }
    }
  }

  /// Advances every animation by [dt] seconds. True while anything is still moving.
  bool advance(double dt, {required bool iconAnimation, required int iconAnimationMs}) {
    dt = dt.clamp(0.0, 0.1);
    var animating = false;
    final dur = (iconAnimation ? iconAnimationMs : 1) / 1000;
    final step = dt / math.max(dur, 0.001);
    final fadeStep = dt / 0.18;
    final slideK = 1 - math.exp(-14 * dt);
    for (var i = items.length - 1; i >= 0; i--) {
      final it = items[i];
      final target = it.leaving ? 0.0 : 1.0;
      if (it.appear != target) {
        it.appear = it.appear < target ? math.min(target, it.appear + step) : math.max(target, it.appear - step);
        animating = true;
      }
      if (it.leaving && it.appear <= 0) {
        items.removeAt(i);
        continue;
      }
      final rt = it.entry.running && !it.leaving ? 1.0 : 0.0;
      if (it.runningAlpha != rt) {
        it.runningAlpha = _step(it.runningAlpha, rt, fadeStep);
        animating = true;
      }
      final at = it.entry.active && !it.leaving ? 1.0 : 0.0;
      if (it.activeAlpha != at) {
        it.activeAlpha = _step(it.activeAlpha, at, fadeStep);
        animating = true;
      }
      if (it.slide.abs() > 0.05) {
        it.slide -= it.slide * slideK;
        animating = true;
      } else {
        it.slide = 0;
      }
    }
    return animating;
  }

  static double _step(double v, double target, double step) => v < target ? math.min(target, v + step) : math.max(target, v - step);
}

// ---------------------------------------------------------------------- layout

class DockLayoutResult {
  double plateLeft = 0, plateRight = 0, plateTop = 0, plateBottom = 0, itemsTop = 0;
}

abstract final class DockLayout {
  /// Positions every item and the plate for the current frame. The zoom target is always computed from the
  /// un-magnified row, so there is no feedback loop.
  static DockLayoutResult compute(GeometryParams p, WindowMetrics m, List<DockItem> items, double cursorX, double zoom, [DockLayoutResult? result]) {
    final res = result ?? DockLayoutResult();
    res.plateTop = m.plateTop;
    res.plateBottom = m.plateBottom;
    var span = 0.0, maxPe = 0.0;
    for (final it in items) {
      final pe = it.presence;
      final w = it.isSeparator ? p.separatorWidth : p.iconSize;
      span += (w + p.gap) * pe;
      maxPe = math.max(maxPe, pe);
    }
    span -= p.gap * maxPe;
    span = math.max(0, span);

    final plateW = p.fixedWidth ? math.max(p.fixedWidthValue, span + 2 * p.padX) : span + 2 * p.padX;
    double baseL;
    final baseW = plateW;
    if (p.fixedWidth || p.alignment == 'Center') {
      baseL = (m.windowWidth - plateW) * 0.5;
    } else if (p.alignment == 'Left') {
      baseL = m.shadowMargin + m.overshoot;
    } else {
      baseL = m.windowWidth - m.shadowMargin - m.overshoot - plateW;
    }
    final l0 = p.fixedWidth || p.alignment == 'Center'
        ? baseL + (baseW - span) * 0.5
        : p.alignment == 'Left'
            ? baseL + p.padX
            : baseL + baseW - p.padX - span;

    final r = math.max(1.0, p.radius);
    final a = (p.maxScale - 1) * Magnification.normalisation(p.iconSize, r) * zoom;
    final warp = a > 0.0005;
    final iconBottom = m.plateBottom - p.iconBottomInset;

    var itemsL = double.maxFinite, itemsR = -double.maxFinite, itemsT = double.maxFinite;
    var x = l0;
    var precedingPresence = 0.0;
    for (final it in items) {
      final pe = it.presence;
      // A gap before the slot, weighted by the visible prefix: a zero-sized ghost cannot introduce a full gap.
      x += p.gap * math.min(precedingPresence, pe);
      final w = (it.isSeparator ? p.separatorWidth : p.iconSize) * pe;
      final b = x;
      var l = b, rr = b + w;
      if (warp) {
        l += a * Magnification.bellInt(l - cursorX, r);
        rr += a * Magnification.bellInt(rr - cursorX, r);
      }
      x += w;
      precedingPresence = math.max(precedingPresence, pe);

      if (it.pendingSlide && it.hasLastBase) it.slide = it.lastBaseX - b;
      it.pendingSlide = false;
      it.lastBaseX = b;
      it.hasLastBase = true;
      it.baseX = b;

      final size = rr - l;
      var px = l + it.slide;
      if (it.dragX != null) px = it.dragX! - size * 0.5;
      it.x = px;
      it.size = size;
      it.bottom = iconBottom;
      itemsL = math.min(itemsL, px);
      itemsR = math.max(itemsR, px + size);
      if (!it.isSeparator) itemsT = math.min(itemsT, iconBottom - size);
    }

    if (items.isEmpty || itemsL > itemsR) {
      itemsL = baseL + p.padX;
      itemsR = baseL + baseW - p.padX;
    }
    if (p.fixedWidth) {
      res.plateLeft = math.min(baseL, itemsL - p.padX);
      res.plateRight = math.max(baseL + baseW, itemsR + p.padX);
    } else {
      res.plateLeft = itemsL - p.padX;
      res.plateRight = itemsR + p.padX;
    }
    res.itemsTop = itemsT == double.maxFinite ? m.plateTop : itemsT;
    return res;
  }

  /// Index of the non-separator item under (x,y), or -1. Gaps belong to the nearest icon, so there are no dead pixels.
  static int hitTest(List<DockItem> items, GeometryParams p, WindowMetrics m, double x, double y) {
    if (y < m.plateTop - (p.iconSize * (p.maxScale - 1)) || y > m.plateBottom + 2) return -1;
    final half = p.gap * 0.5;
    for (var i = 0; i < items.length; i++) {
      final it = items[i];
      if (it.isSeparator || it.leaving || it.presence < 0.3) continue;
      if (x >= it.x - half && x < it.x + it.size + half) {
        if (y < m.plateTop && y < it.top) return -1; // above the plate only the icon itself counts
        return i;
      }
    }
    return -1;
  }
}
