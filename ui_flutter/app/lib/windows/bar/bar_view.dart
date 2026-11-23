import 'dart:async';

import 'package:flutter/material.dart';

import 'dart:math' as math;

import '../../core/app_state.dart';
import '../../core/blur.dart';
import '../../core/sfx.dart';
import '../../core/theme.dart';

const double barHeight = 36, barTopInset = 4, barBottomInset = 4, barSideInset = 12;

/// Total height of the bar window (and of the strip of the screen it reserves).
const double barWindowHeight = barHeight + barTopInset + barBottomInset;

/// The "Island" style: one dark panel hanging from the top of the screen (flush with it), rounded under its ends, a little
/// narrower than the screen; the groups are capsules on it. [barIslandHeight] tall, [barIslandRadius] under the corners.
const double barIslandHeight = 34, barIslandRadius = 12;

/// How wide the "Island" panel is on a monitor [windowWidth] dip wide: about three quarters of it, centred.
double barIslandWidth(double windowWidth) => math.min(windowWidth, math.max(math.min(windowWidth, 880.0), windowWidth * .74));

/// Height of the bar window (and of the strip it reserves) for a [style].
double barWindowHeightFor(String style) => switch (style) {
      'Island' => barIslandHeight,
      'Bar' => barEdgeHeight + barEdgeRadius,
      'Text' => barTextHeight,
      _ => barWindowHeight,
    };

/// The "Text" style: no panel and no islands, only text over the desktop, in a transparent window this tall.
const double barTextHeight = 26;

/// The "Bar" style: a panel flush with the top and the sides of the screen, [barEdgeHeight] tall. Below its two ends the panel curls
/// down into the screen's side in a concave corner of [barEdgeRadius]; that strip is part of the window, but only the panel is reserved.
const double barEdgeHeight = 32, barEdgeRadius = 14;

/// How much of the screen's top the bar keeps for itself (what windows must stay clear of) for a [style].
double barReservedHeightFor(String style) => switch (style) {
      'Island' => barIslandHeight,
      'Bar' => barEdgeHeight,
      'Text' => barTextHeight,
      _ => barWindowHeight,
    };

/// How far down from the top edge the pointer counts as being over the bar (to reveal an auto-hidden one) for a [style].
double barHoverHeight(String style) => switch (style) {
      'Bar' => barEdgeHeight,
      'Island' => barIslandHeight,
      'Text' => barTextHeight,
      _ => barWindowHeightFor(style) - barBottomInset,
    };

/// What the controller tells one bar window about itself.
class BarModel extends ChangeNotifier {
  BarModel(this.monitorKey);
  final String monitorKey;

  /// Physical position of the window's top-left corner and its scale: used to turn a button's rectangle into screen pixels.
  Offset origin = Offset.zero;
  double scale = 1;
  double widthDip = 1280;

  /// Slide target: true once the bar has left the screen (auto-hide, fullscreen).
  final hidden = ValueNotifier<bool>(false);
  bool hover = false;
  DateTime lastHover = DateTime.now();

  /// Which menu is open from this bar (its button stays highlighted).
  String? openKind;

  /// Workspace buttons (empty when tiling is off): the active one is filled.
  int workspaces = 0, activeWorkspace = 1;

  void Function(String kind, Rect anchorPx)? onMenu;
  void Function()? onSearch;
  void Function(String uri)? onOpenUri;
  void Function(int workspace)? onWorkspace;
  void Function()? onPointerEnter;

  Rect anchorPx(Rect local) => Rect.fromLTWH(origin.dx + local.left * scale, origin.dy + local.top * scale, local.width * scale, local.height * scale);

  void changed() => notifyListeners();
}

class _MediaInfo {
  _MediaInfo(Map m)
      : title = '${m['title']}',
        artist = '${m['artist'] ?? ''}',
        playing = m['playing'] == true,
        canToggle = m['canToggle'] == true,
        canNext = m['canNext'] == true,
        canPrevious = m['canPrevious'] == true;
  _MediaInfo._(this.title, this.artist, this.playing, this.canToggle, this.canNext, this.canPrevious);
  final String title, artist;
  final bool playing, canToggle, canNext, canPrevious;

  _MediaInfo withPlaying(bool value) => _MediaInfo._(title, artist, value, canToggle, canNext, canPrevious);

  bool same(_MediaInfo? o) =>
      o != null && o.title == title && o.artist == artist && o.playing == playing && o.canToggle == canToggle && o.canNext == canNext && o.canPrevious == canPrevious;
}

/// The top bar: a glass strip with rounded tonal islands. Left the app and what is playing, centre the workspaces,
/// right the status groups and the clock. Always a dark shell, drawn from the seed colour.
class BarView extends StatefulWidget {
  const BarView({super.key, required this.model, this.now = DateTime.now});
  final BarModel model;

  /// The clock; tests pass a fixed time.
  final DateTime Function() now;

  @override
  State<BarView> createState() => _BarViewState();
}

class _BarViewState extends State<BarView> with SingleTickerProviderStateMixin {
  late final AnimationController _slide = AnimationController(vsync: this, value: 0);
  Timer? _clockTimer, _mediaTimer, _batteryTimer;
  StreamSubscription<dynamic>? _events;
  String _time = '', _date = '', _language = '';
  _MediaInfo? _media;
  bool _mediaBusy = false, _online = true;
  Map? _battery;
  AppState? _state;

  /// "Island" and "Bar" draw one panel for the whole bar: the groups lose their own backgrounds and are told apart by thin dividers.
  bool _merged = false;

  BarModel get m => widget.model;

  @override
  void initState() {
    super.initState();
    m.hidden.addListener(_onHidden);
    m.addListener(_rebuild);
    _tickClock();
    _clockTimer = Timer.periodic(const Duration(seconds: 1), (_) => _tickClock());
    _mediaTimer = Timer.periodic(const Duration(seconds: 2), (_) => _readMedia());
    _batteryTimer = Timer.periodic(const Duration(seconds: 30), (_) => _readBattery());
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_state == null) {
      _state = AppScope.read(context);
      _events = _state!.eventsOther.listen((e) {
        if (e.name == 'language.changed') setState(() => _language = '${(e.data as Map)['tag']}');
        if (e.name == 'network.changed') setState(() => _online = (e.data as Map)['available'] == true);
      });
      WidgetsBinding.instance.addPostFrameCallback((_) {
        _readMedia();
        _readBattery();
        _state!.call('language.current').then((r) {
          if (mounted && r is Map) setState(() => _language = '${r['tag']}');
        }).catchError((_) => null);
        _state!.call('network.available').then((r) {
          if (mounted && r is bool) setState(() => _online = r);
        }).catchError((_) => null);
      });
    }
  }

  @override
  void dispose() {
    m.hidden.removeListener(_onHidden);
    m.removeListener(_rebuild);
    _clockTimer?.cancel();
    _mediaTimer?.cancel();
    _batteryTimer?.cancel();
    _mediaFollowUp?.cancel();
    _events?.cancel();
    _slide.dispose();
    super.dispose();
  }

  void _rebuild() => setState(() {});

  void _onHidden() {
    final s = AppScope.read(context);
    final hide = m.hidden.value;
    final ms = s.setting<int>(hide ? 'TopBarHideMs' : 'TopBarShowMs', hide ? 220 : 170);
    _slide.animateTo(hide ? 1 : 0, duration: Duration(milliseconds: ms), curve: Curves.easeInOutCubic);
  }

  void _tickClock() {
    final s = _state;
    final now = widget.now();
    final seconds = s?.setting<bool>('TopBarShowSeconds', false) ?? false;
    String two(int v) => v.toString().padLeft(2, '0');
    final time = '${two(now.hour)}:${two(now.minute)}${seconds ? ':${two(now.second)}' : ''}';
    final date = '${two(now.day)}/${two(now.month)}';
    if (time != _time || date != _date) {
      setState(() {
        _time = time;
        _date = date;
      });
    }
  }

  Future<void> _readMedia() async {
    final s = _state;
    if (s == null || _mediaBusy || !mounted || !s.setting<bool>('TopBarMediaEnabled', true)) return;
    if (m.hidden.value && _slide.isCompleted) return;
    _mediaBusy = true;
    try {
      final r = await s.call('media.get') as Map?;
      final next = r != null && '${r['title']}'.trim().isNotEmpty ? _MediaInfo(r) : null;
      // Nothing is rebuilt (and nothing repainted) when the answer is the same as the last one.
      if (mounted && !(next == null ? _media == null : next.same(_media))) setState(() => _media = next);
    } catch (_) {
      if (mounted && _media != null) setState(() => _media = null);
    } finally {
      _mediaBusy = false;
    }
  }

  Future<void> _readBattery() async {
    try {
      final r = await _state?.call('battery.get') as Map?;
      if (mounted && '$r' != '$_battery') setState(() => _battery = r);
    } catch (_) {}
  }

  Future<void> _mediaAct(String command) async {
    final current = _media;
    if (command == 'toggle' && current != null) setState(() => _media = current.withPlaying(!current.playing));   // answers at once
    try {
      await _state!.call('media.control', {'command': command});
    } catch (_) {}
    await _readMedia();
    _mediaFollowUp?.cancel();
    _mediaFollowUp = Timer(const Duration(milliseconds: 350), _readMedia);   // players confirm a moment after the command
  }

  Timer? _mediaFollowUp;

  @override
  Widget build(BuildContext context) {
    final s = AppScope.of(context);
    final bar = s.darkScheme;
    final theme = buildTheme(bar);
    final autoHide = s.setting<bool>('TopBarAutoHide', false);
    final style = s.setting<String>('TopBarStyle', 'Pills');
    _merged = style == 'Island' || style == 'Bar';
    return Theme(
      data: theme,
      child: BlurScope(
        child: Material(
        type: MaterialType.transparency,
        child: MouseRegion(
          onEnter: (_) {
            m.hover = true;
            m.onPointerEnter?.call();
          },
          onExit: (_) {
            m.hover = false;
            m.lastHover = DateTime.now();
          },
          child: AnimatedBuilder(
            animation: _slide,
            builder: (context, child) {
              final e = Curves.easeInOut.transform(_slide.value);
              BlurScope.maybeOf(context)?.markDirty();
              return Transform.translate(offset: Offset(0, -(style == 'Bar' || style == 'Text' || style == 'Island' ? barWindowHeightFor(style) : barHeight + barTopInset) * e), child: child);
            },
            child: _frame(context, s, bar, autoHide, style),
          ),
        ),
        ),
      ),
    );
  }

  /// What the bar sits on: nothing (islands hanging over the desktop), one floating panel, or a panel flush with the screen's top and sides.
  Widget _frame(BuildContext context, AppState s, ColorScheme bar, bool autoHide, String style) {
    if (style == 'Text') {
      return SizedBox(
        height: barTextHeight,
        child: Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: _textContent(s, bar)),
      );
    }
    final content = style == 'Island' ? const SizedBox.shrink() : _content(context, s, bar, autoHide);
    final fill = bar.surface;
    switch (style) {
      case 'Island':
        // Flush with the top of the screen, rounded only underneath, narrower than the screen (as in the reference).
        return Align(
          alignment: Alignment.topCenter,
          child: Container(
            width: barIslandWidth(m.widthDip),
            height: barIslandHeight,
            padding: const EdgeInsets.symmetric(horizontal: 4),
            decoration: BoxDecoration(color: fill, borderRadius: const BorderRadius.vertical(bottom: Radius.circular(barIslandRadius))),
            child: _islandContent(s, bar, barIslandWidth(m.widthDip) - 8),
          ),
        );
      case 'Bar':
        return SizedBox(
          height: barWindowHeightFor(style),
          child: Stack(children: [
            Positioned.fill(child: CustomPaint(painter: _EdgePanelPainter(fill))),
            Positioned(left: barSideInset, right: barSideInset, top: 1, height: barEdgeHeight - 2, child: content),
          ]),
        );
      default:
        // No backing strip: only the islands hang over the desktop.
        return Padding(
          padding: const EdgeInsets.fromLTRB(barSideInset, barTopInset, barSideInset, barBottomInset),
          child: SizedBox(height: barHeight, child: content),
        );
    }
  }

  /// The "Text" style: nothing but words, no backgrounds, icons or dividers. The app name opens the main menu, what is playing
  /// opens the player, the numbers are the workspaces, the input language and the clock open their usual menus.
  Widget _textContent(AppState s, ColorScheme bar) {
    final showMedia = s.setting<bool>('TopBarMediaEnabled', true);
    final showDate = s.setting<bool>('TopBarShowDate', false);
    final name = s.foregroundName == null ? tr('Desktop') : tr(s.foregroundName!);
    final power = _battery;
    final media = showMedia ? _media : null;
    Widget item(String text, {String? kind, VoidCallback? onTap, String? tooltip, double size = 13, FontWeight weight = FontWeight.w500, double opacity = 1, double maxWidth = 360, String? key}) =>
        _BarButton(
          kind: kind,
          model: m,
          bar: bar,
          tooltip: tooltip ?? '',
          onTap: onTap,
          plain: true,
          child: ConstrainedBox(
            constraints: BoxConstraints(maxWidth: maxWidth),
            child: _SizedLabel(
              keyText: key ?? text,
              child: Text(text, maxLines: 1, overflow: TextOverflow.ellipsis, style: _plainText(bar, size: size, weight: weight, opacity: opacity)),
            ),
          ),
        );
    final left = Row(mainAxisSize: MainAxisSize.min, children: [
      item(name, kind: 'main', tooltip: 'FloatingDock menu', weight: FontWeight.w700),
      AnimatedSize(
        duration: _labelMotion,
        curve: _labelCurve,
        alignment: Alignment.centerLeft,
        child: media != null
            ? Row(mainAxisSize: MainAxisSize.min, children: [
                item(media.artist.isEmpty ? media.title : '${media.title} — ${media.artist}',
                    kind: 'media', tooltip: 'Media', size: 12, opacity: media.playing ? .85 : .5, maxWidth: 420),
              ])
            : const SizedBox(height: 26),
      ),
    ]);
    final center = m.workspaces > 0
        ? Row(mainAxisSize: MainAxisSize.min, children: [
            for (var i = 1; i <= m.workspaces; i++)
              item('$i',
                  onTap: () => m.onWorkspace?.call(i),
                  tooltip: trf('Workspace {0}', [i]),
                  weight: i == m.activeWorkspace ? FontWeight.w700 : FontWeight.w500,
                  opacity: i == m.activeWorkspace ? 1 : .5),
          ])
        : const SizedBox.shrink();
    final right = Row(mainAxisSize: MainAxisSize.min, children: [
      if (power != null && power['present'] == true)
        item('${power['charging'] == true ? '↯ ' : ''}${power['percent']}%',
            onTap: () => m.onOpenUri?.call('ms-settings:batterysaver'), tooltip: 'Battery settings', size: 12, opacity: .85),
      item(_language.isEmpty ? '—' : _language, kind: 'language', tooltip: 'Input language', size: 12, weight: FontWeight.w600, opacity: .85),
      item(showDate ? '$_date  $_time' : _time, kind: 'calendar', tooltip: 'Date and time', weight: FontWeight.w600),
    ]);
    return Stack(alignment: Alignment.center, children: [
      Align(alignment: Alignment.centerLeft, child: left),
      Align(alignment: Alignment.center, child: center),
      Align(alignment: Alignment.centerRight, child: right),
    ]);
  }

  static const _weekdaysEn = ['MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT', 'SUN'];
  static const _weekdaysRu = ['ПН', 'ВТ', 'СР', 'ЧТ', 'ПТ', 'СБ', 'ВС'];
  static const _monthsEn = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'];
  static const _monthsRu = ['ЯНВ', 'ФЕВ', 'МАР', 'АПР', 'МАЙ', 'ИЮН', 'ИЮЛ', 'АВГ', 'СЕН', 'ОКТ', 'НОЯ', 'ДЕК'];

  /// The "Island" layout, drawn after the reference: on the left settings and search, the workspaces as dots, the app name and
  /// what is playing; then the clock (hour large, minutes and seconds stacked) and the date (day large, weekday and month stacked);
  /// on the right the tray, the input language and the status pills (network and sound in the accent colour, the battery in white).
  Widget _islandContent(AppState s, ColorScheme bar, double width) {
    final showMedia = s.setting<bool>('TopBarMediaEnabled', true);
    final showTray = s.setting<bool>('TopBarTrayEnabled', true);
    final showSearch = s.setting<bool>('TopBarSearchEnabled', true);
    final showDate = s.setting<bool>('TopBarShowDate', false);
    final seconds = s.setting<bool>('TopBarShowSeconds', false);
    final russian = s.setting<String>('Language', 'English') == 'Russian';
    final name = s.foregroundName == null ? tr('Desktop') : tr(s.foregroundName!);
    final power = _battery;
    final now = widget.now();
    String two(int v) => v.toString().padLeft(2, '0');
    final mediaWidth = (width - 760).clamp(80.0, 230.0);

    Widget cap(List<Widget> children) => _Island(bar: bar, merged: true, children: children);
    TextStyle small(double size, {FontWeight weight = FontWeight.w700, Color? color}) =>
        TextStyle(fontSize: size, fontWeight: weight, height: 1.0, color: color ?? bar.onSurface);

    Widget dot(int i) {
      final active = i == m.activeWorkspace;
      return _BarButton(
        model: m,
        bar: bar,
        tooltip: trf('Workspace {0}', [i]),
        onTap: () => m.onWorkspace?.call(i),
        padding: const EdgeInsets.symmetric(horizontal: 3),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 180),
          width: active ? 30 : 12,
          height: 12,
          decoration: BoxDecoration(color: active ? bar.primary : bar.surfaceContainerHighest, borderRadius: BorderRadius.circular(6)),
        ),
      );
    }

    final media = showMedia ? _media : null;
    final left = <Widget>[
      cap([
        // In this layout the gear is where the reference has its settings: it opens the program's own settings window.
        _BarButton(model: m, bar: bar, tooltip: 'FloatingDock settings', icon: Icons.settings_rounded, iconSize: 16, onTap: () => _state?.act('settings')),
        if (showSearch) _BarButton(model: m, bar: bar, tooltip: 'Search', icon: Icons.search_rounded, iconSize: 16, onTap: () => m.onSearch?.call()),
      ]),
      if (m.workspaces > 0) cap([for (var i = 1; i <= m.workspaces; i++) dot(i)]),
      cap([
        _BarButton(
          kind: 'main',
          model: m,
          bar: bar,
          tooltip: 'FloatingDock menu',
          padding: const EdgeInsets.symmetric(horizontal: 10),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 180),
            child: _SizedLabel(keyText: name, child: Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: small(11.5))),
          ),
        ),
      ]),
      // The capsule sits right of the name: while the name changes length it slides along.
      AnimatedSize(
        duration: _labelMotion,
        curve: _labelCurve,
        alignment: Alignment.centerLeft,
        child: media == null
            ? const SizedBox(height: 26)
            : Padding(
                padding: const EdgeInsets.only(right: 0),
                child: cap([
                  Container(
                    width: 20,
                    height: 20,
                    margin: const EdgeInsets.only(left: 3, right: 2),
                    decoration: BoxDecoration(color: bar.primaryContainer, shape: BoxShape.circle),
                    child: Icon(Icons.music_note_rounded, size: 12, color: bar.onPrimaryContainer),
                  ),
                  _BarButton(
                    kind: 'media',
                    model: m,
                    bar: bar,
                    tooltip: 'Media',
                    padding: const EdgeInsets.symmetric(horizontal: 6),
                    child: ConstrainedBox(
                      constraints: BoxConstraints(maxWidth: mediaWidth),
                      child: _SizedLabel(
                        keyText: '${media.title} ${media.artist}',
                        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
                          Text(media.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: small(10.5)),
                          if (media.artist.isNotEmpty) Padding(padding: const EdgeInsets.only(top: 2), child: Text(media.artist, maxLines: 1, overflow: TextOverflow.ellipsis, style: small(9, weight: FontWeight.w500, color: bar.onSurfaceVariant))),
                        ]),
                      ),
                    ),
                  ),
                  _BarButton(model: m, bar: bar, tooltip: 'Previous', icon: Icons.skip_previous_rounded, iconSize: 15, width: 24, enabled: media.canPrevious, onTap: () => _mediaAct('previous')),
                  _BarButton(model: m, bar: bar, tooltip: 'Play / pause', icon: media.playing ? Icons.pause_rounded : Icons.play_arrow_rounded, iconSize: 16, width: 28, fill: bar.surfaceContainerHighest, enabled: media.canToggle, onTap: () => _mediaAct('toggle')),
                  _BarButton(model: m, bar: bar, tooltip: 'Next', icon: Icons.skip_next_rounded, iconSize: 15, width: 24, enabled: media.canNext, onTap: () => _mediaAct('next')),
                ]),
              ),
      ),
    ];

    final clock = <Widget>[
      cap([
        _BarButton(
          kind: 'calendar',
          model: m,
          bar: bar,
          tooltip: 'Date and time',
          padding: const EdgeInsets.symmetric(horizontal: 9),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Text(two(now.hour), style: small(17, weight: FontWeight.w800)),
            const SizedBox(width: 3),
            Column(mainAxisSize: MainAxisSize.min, mainAxisAlignment: MainAxisAlignment.center, children: [
              Text(two(now.minute), style: small(9.5)),
              if (seconds) Padding(padding: const EdgeInsets.only(top: 2), child: Text(two(now.second), style: small(9.5, color: bar.onSurfaceVariant))),
            ]),
            if (showDate) ...[
              Container(width: 1, height: 14, margin: const EdgeInsets.symmetric(horizontal: 8), color: bar.outlineVariant.withValues(alpha: .6)),
              Text('${now.day}', style: small(17, weight: FontWeight.w800)),
              const SizedBox(width: 3),
              Column(mainAxisSize: MainAxisSize.min, mainAxisAlignment: MainAxisAlignment.center, children: [
                Text((russian ? _weekdaysRu : _weekdaysEn)[now.weekday - 1], style: small(8.5, color: bar.onSurfaceVariant)),
                Padding(padding: const EdgeInsets.only(top: 1), child: Text((russian ? _monthsRu : _monthsEn)[now.month - 1], style: small(8.5))),
              ]),
            ],
          ]),
        ),
      ]),
    ];

    const pill = 13.0;
    final right = <Widget>[
      if (showTray) cap([_BarButton(kind: 'tray', model: m, bar: bar, tooltip: 'System tray', icon: Icons.expand_more_rounded, iconSize: 18)]),
      cap([
        _BarButton(
          kind: 'language',
          model: m,
          bar: bar,
          tooltip: 'Input language',
          padding: const EdgeInsets.symmetric(horizontal: 8),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Icon(Icons.keyboard_rounded, size: 15, color: bar.onSurfaceVariant),
            const SizedBox(width: 6),
            Text(_language.isEmpty ? '—' : _language, style: small(10.5)),
          ]),
        ),
      ]),
      _BarButton(kind: 'network', model: m, bar: bar, tooltip: 'Network', width: 36, radius: pill, fill: bar.primary, child: Opacity(opacity: _online ? 1 : .4, child: Icon(Icons.wifi_rounded, size: 16, color: bar.onPrimary))),
      _BarButton(kind: 'sound', model: m, bar: bar, tooltip: 'Sound', width: 36, radius: pill, icon: Icons.volume_up_rounded, iconSize: 16, fill: bar.primary, iconColor: bar.onPrimary),
      if (power != null && power['present'] == true)
        _BarButton(
          model: m,
          bar: bar,
          tooltip: 'Battery settings',
          onTap: () => m.onOpenUri?.call('ms-settings:batterysaver'),
          radius: pill,
          fill: bar.onSurface,
          padding: const EdgeInsets.symmetric(horizontal: 9),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Icon(power['charging'] == true ? Icons.battery_charging_full_rounded : Icons.battery_full_rounded, size: 15, color: bar.surface),
            const SizedBox(width: 3),
            Text('${power['percent']}%', style: small(10.5, color: bar.surface)),
          ]),
        ),
    ];

    Widget row(List<Widget> items) => Row(mainAxisSize: MainAxisSize.min, children: [for (var i = 0; i < items.length; i++) ...[if (i > 0) const SizedBox(width: 5), items[i]]]);
    return Row(children: [
      const SizedBox(width: 4),
      row([...left, ...clock]),
      const Spacer(),
      row(right),
      const SizedBox(width: 4),
    ]);
  }

  /// Between two groups: a small gap (every group is a capsule of its own, on the desktop or on the shared panel).
  Widget _gap(ColorScheme bar, {double width = 6}) => SizedBox(width: width);

  Widget _content(BuildContext context, AppState s, ColorScheme bar, bool autoHide) {
    final showMedia = s.setting<bool>('TopBarMediaEnabled', true);
    final showTray = s.setting<bool>('TopBarTrayEnabled', true);
    final showSearch = s.setting<bool>('TopBarSearchEnabled', true);
    final showDate = s.setting<bool>('TopBarShowDate', false);
    final name = s.foregroundName == null ? tr('Desktop') : tr(s.foregroundName!);
    final power = _battery;
    final left = Row(mainAxisSize: MainAxisSize.min, children: [
      SizedBox(width: _merged ? 0 : 5),
      _Island(bar: bar, merged: _merged, children: [
        _BarButton(kind: 'main', model: m, bar: bar, tooltip: 'FloatingDock menu', child: Container(width: 12, height: 12, decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: bar.onSurface, width: 2)))),
        ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 360),
          child: Padding(
            padding: const EdgeInsets.only(right: 12, left: 2),
            child: _SizedLabel(
              keyText: name,
              child: Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: bar.onSurface)),
            ),
          ),
        ),
      ]),
      // The capsule sits right of the name island: while that island grows or shrinks it slides along with it.
      AnimatedSize(
        duration: _labelMotion,
        curve: _labelCurve,
        alignment: Alignment.centerLeft,
        child: showMedia && _media != null
            ? Row(mainAxisSize: MainAxisSize.min, children: [_gap(bar, width: 8), _mediaCapsule(bar, _media!)])
            : const SizedBox(height: 30),
      ),
    ]);
    final right = Row(mainAxisSize: MainAxisSize.min, children: [
      if (showSearch || showTray)
        _Island(bar: bar, merged: _merged, children: [
          if (showSearch) _BarButton(model: m, bar: bar, tooltip: 'Search', icon: Icons.search_rounded, onTap: () => m.onSearch?.call()),
          if (showTray) _BarButton(kind: 'tray', model: m, bar: bar, tooltip: 'System tray', icon: Icons.expand_more_rounded, iconSize: 18),
        ]),
      _gap(bar),
      _Island(bar: bar, merged: _merged, children: [
        if (power != null && power['present'] == true)
          _BarButton(
            model: m,
            bar: bar,
            tooltip: 'Battery settings',
            onTap: () => m.onOpenUri?.call('ms-settings:batterysaver'),
            fill: bar.onSurface,
            child: Text('${power['charging'] == true ? '↯ ' : ''}${power['percent']}%', style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: bar.surface)),
          ),
        _BarButton(kind: 'network', model: m, bar: bar, tooltip: 'Network', fill: bar.primary, child: Opacity(opacity: _online ? 1 : .4, child: Icon(Icons.wifi_rounded, size: 16, color: bar.onPrimary))),
        _BarButton(kind: 'sound', model: m, bar: bar, tooltip: 'Sound', icon: Icons.volume_up_rounded, fill: bar.primary, iconColor: bar.onPrimary),
        _BarButton(kind: 'language', model: m, bar: bar, tooltip: 'Input language', child: Text(_language.isEmpty ? '—' : _language, style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w700, color: bar.onSurface))),
      ]),
      _gap(bar),
      _Island(bar: bar, merged: _merged, children: [
        _BarButton(model: m, bar: bar, tooltip: 'Windows settings', icon: Icons.settings_rounded, onTap: () => m.onOpenUri?.call('ms-settings:')),
        _BarButton(
          kind: 'calendar',
          model: m,
          bar: bar,
          tooltip: 'Date and time',
          padding: const EdgeInsets.symmetric(horizontal: 8),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            if (showDate) ...[Text(_date, style: TextStyle(fontSize: 11, color: bar.onSurfaceVariant)), const SizedBox(width: 8)],
            Text(_time, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: bar.onSurface)),
          ]),
        ),
      ]),
      SizedBox(width: _merged ? 0 : 5),
    ]);
    final center = m.workspaces > 0 ? _workspaces(bar) : const SizedBox.shrink();
    return Stack(alignment: Alignment.center, children: [
      Align(alignment: Alignment.centerLeft, child: left),
      Align(alignment: Alignment.center, child: center),
      Align(alignment: Alignment.centerRight, child: right),
    ]);
  }

  Widget _workspaces(ColorScheme bar) => Row(mainAxisSize: MainAxisSize.min, children: [
        _Island(bar: bar, merged: _merged, children: [
          for (var i = 1; i <= m.workspaces; i++)
            _BarButton(
              model: m,
              bar: bar,
              tooltip: trf('Workspace {0}', [i]),
              onTap: () => m.onWorkspace?.call(i),
              fill: i == m.activeWorkspace ? bar.primary : null,
              child: Text('$i', style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: i == m.activeWorkspace ? bar.onPrimary : bar.onSurfaceVariant)),
            ),
        ]),
        Padding(
          padding: const EdgeInsets.only(left: 8),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(tr('Desktop'), style: TextStyle(fontSize: 9.5, color: bar.onSurfaceVariant, height: 1.1)),
            Text(trf('Workspace {0}', [m.activeWorkspace]), style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: bar.onSurface, height: 1.1)),
          ]),
        ),
      ]);

  Widget _mediaCapsule(ColorScheme bar, _MediaInfo info) => _Island(bar: bar, merged: _merged, children: [
        Container(
          width: 24,
          height: 24,
          margin: const EdgeInsets.only(left: 3, right: 2),
          decoration: BoxDecoration(color: bar.primaryContainer, shape: BoxShape.circle),
          child: Icon(Icons.music_note_rounded, size: 13, color: bar.onPrimaryContainer),
        ),
        _BarButton(
          kind: 'media',
          model: m,
          bar: bar,
          tooltip: 'Media',
          padding: const EdgeInsets.only(left: 6, right: 8),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 190),
            child: _SizedLabel(
              keyText: '${info.title} ${info.artist}',
              child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(info.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: bar.onSurface, height: 1.1)),
                if (info.artist.isNotEmpty) Text(info.artist, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 9.5, color: bar.onSurfaceVariant, height: 1.1)),
              ]),
            ),
          ),
        ),
        _BarButton(model: m, bar: bar, tooltip: 'Play / pause', icon: info.playing ? Icons.pause_rounded : Icons.play_arrow_rounded, iconSize: 16, width: 26, fill: bar.surfaceContainerHighest, iconColor: bar.onSurface, enabled: info.canToggle, onTap: () => _mediaAct('toggle')),
        _BarButton(model: m, bar: bar, tooltip: 'Next', icon: Icons.skip_next_rounded, iconSize: 16, width: 26, enabled: info.canNext, onTap: () => _mediaAct('next')),
      ]);
}

const _labelMotion = Duration(milliseconds: 300);
const _labelCurve = Curves.easeOutCubic;

/// Words straight over the desktop: light, with a soft shadow so they stay readable on a bright wallpaper.
TextStyle _plainText(ColorScheme bar, {double size = 13, FontWeight weight = FontWeight.w500, double opacity = 1}) => TextStyle(
      fontSize: size,
      fontWeight: weight,
      height: 1.15,
      color: Colors.white.withValues(alpha: opacity),
      shadows: const [Shadow(color: Color(0xA6000000), blurRadius: 3, offset: Offset(0, 1)), Shadow(color: Color(0x59000000), blurRadius: 8)],
    );

/// A label whose island resizes smoothly when the text changes: the old text fades out, the new one fades in, and the width
/// glides between them. Whatever sits next to it is pushed along because the box really changes size on every frame.
class _SizedLabel extends StatelessWidget {
  const _SizedLabel({required this.keyText, required this.child});
  final String keyText;
  final Widget child;

  @override
  Widget build(BuildContext context) => AnimatedSize(
        duration: _labelMotion,
        curve: _labelCurve,
        alignment: Alignment.centerLeft,
        child: AnimatedSwitcher(
          duration: const Duration(milliseconds: 200),
          switchInCurve: Curves.easeOut,
          switchOutCurve: Curves.easeIn,
          // Only the current text takes part in the layout, so the width follows the new text at once and AnimatedSize eases it.
          layoutBuilder: (current, previous) => Stack(
            clipBehavior: Clip.none,
            alignment: Alignment.centerLeft,
            children: [for (final p in previous) Positioned(left: 0, child: p), if (current != null) current],
          ),
          child: KeyedSubtree(key: ValueKey(keyText), child: child),
        ),
      );
}

/// A rounded tonal island that holds a few related buttons.
/// The panel of the "Bar" style: a rectangle across the top and, under each end, a concave corner that joins it to the screen's side.
class _EdgePanelPainter extends CustomPainter {
  const _EdgePanelPainter(this.color);
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    const h = barEdgeHeight, r = barEdgeRadius;
    final path = Path()
      ..addRect(Rect.fromLTWH(0, 0, size.width, h))
      ..moveTo(0, h)
      ..lineTo(r, h)
      ..arcTo(Rect.fromCircle(center: const Offset(r, h + r), radius: r), -math.pi / 2, -math.pi / 2, false)
      ..close()
      ..moveTo(size.width, h)
      ..lineTo(size.width - r, h)
      ..arcTo(Rect.fromCircle(center: Offset(size.width - r, h + r), radius: r), -math.pi / 2, math.pi / 2, false)
      ..close();
    canvas.drawPath(path, Paint()..color = color..isAntiAlias = true);
  }

  @override
  bool shouldRepaint(_EdgePanelPainter old) => old.color != color;
}

class _Island extends StatelessWidget {
  const _Island({required this.bar, required this.children, this.merged = false});
  final ColorScheme bar;
  final List<Widget> children;

  /// Inside a shared panel: a lower, subtler capsule (no blur of its own).
  final bool merged;

  @override
  Widget build(BuildContext context) {
    final row = Row(mainAxisSize: MainAxisSize.min, children: [for (var i = 0; i < children.length; i++) ...[if (i > 0) const SizedBox(width: 2), children[i]]]);
    // Inside the shared panel a capsule is a touch lower and a touch lighter than the panel; on the desktop it is opaque.
    final capsule = Container(
      height: merged ? 26 : 30,
      padding: const EdgeInsets.symmetric(horizontal: 2),
      decoration: BoxDecoration(color: merged ? bar.surfaceContainer : bar.surfaceContainerHigh, borderRadius: BorderRadius.circular(12)),
      child: row,
    );
    return merged ? capsule : BlurSurface(radius: 12, child: capsule);
  }
}

/// A 26 dp pill button with hover and pressed state layers; opens a menu of [kind] or calls [onTap].
class _BarButton extends StatefulWidget {
  const _BarButton({
    required this.model,
    required this.bar,
    required this.tooltip,
    this.kind,
    this.onTap,
    this.icon,
    this.iconSize = 15,
    this.child,
    this.padding = const EdgeInsets.symmetric(horizontal: 8),
    this.width,
    this.fill,
    this.iconColor,
    this.enabled = true,
    this.plain = false,
    this.radius = 10,
  });
  final BarModel model;
  final ColorScheme bar;
  final String tooltip;
  final String? kind;
  final VoidCallback? onTap;
  final IconData? icon;
  final double iconSize;
  final Widget? child;
  final EdgeInsets padding;
  final double? width;
  final Color? fill, iconColor;
  final bool enabled;

  /// Corner radius of the button's background (a pill: half its height).
  final double radius;

  /// No background of any kind (the "Text" style): hovering and pressing only dim the content.
  final bool plain;

  @override
  State<_BarButton> createState() => _BarButtonState();
}

class _BarButtonState extends State<_BarButton> {
  bool _hover = false, _down = false;

  void _tap() {
    if (!widget.enabled) return;
    if (widget.kind != null) {
      final box = context.findRenderObject() as RenderBox;
      final rect = box.localToGlobal(Offset.zero) & box.size;
      widget.model.onMenu?.call(widget.kind!, widget.model.anchorPx(rect));
    } else {
      Sfx.play(Sound.tap);
      widget.onTap?.call();
    }
  }

  @override
  Widget build(BuildContext context) {
    final bar = widget.bar;
    final open = widget.kind != null && widget.model.openKind == widget.kind;
    final layer = _down ? .20 : (_hover || open) ? .12 : 0.0;
    final fg = (widget.iconColor ?? bar.onSurface).withValues(alpha: widget.enabled ? 1 : .35);
    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() {
        _hover = false;
        _down = false;
      }),
      cursor: widget.enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTapDown: (_) => setState(() => _down = true),
        onTapCancel: () => setState(() => _down = false),
        onTapUp: (_) => setState(() => _down = false),
        onTap: _tap,
        child: Container(
          height: 26,
          constraints: BoxConstraints(minWidth: widget.width ?? 26),
          padding: widget.width != null ? EdgeInsets.zero : widget.padding,
          alignment: Alignment.center,
          decoration: widget.plain
              ? null
              : BoxDecoration(
                  color: Color.alphaBlend(bar.onSurface.withValues(alpha: layer), widget.fill ?? Colors.transparent),
                  borderRadius: BorderRadius.circular(widget.radius),
                ),
          child: widget.plain
              ? Opacity(opacity: _down ? .5 : (_hover || open) ? .72 : 1, child: widget.child)
              : widget.child ?? Icon(widget.icon, size: widget.iconSize, color: fg),
        ),
      ),
    );
  }
}
