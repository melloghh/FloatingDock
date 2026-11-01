import 'dart:async';

import 'package:flutter/material.dart';

import '../../core/app_state.dart';
import '../../core/theme.dart';

const double barHeight = 36, barTopInset = 4, barBottomInset = 4, barSideInset = 12;

/// Total height of the bar window (and of the strip of the screen it reserves).
const double barWindowHeight = barHeight + barTopInset + barBottomInset;

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
        canNext = m['canNext'] == true;
  final String title, artist;
  final bool playing, canToggle, canNext;
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
      if (mounted) setState(() => _media = r != null && '${r['title']}'.trim().isNotEmpty ? _MediaInfo(r) : null);
    } catch (_) {
      if (mounted) setState(() => _media = null);
    } finally {
      _mediaBusy = false;
    }
  }

  Future<void> _readBattery() async {
    try {
      final r = await _state?.call('battery.get') as Map?;
      if (mounted) setState(() => _battery = r);
    } catch (_) {}
  }

  Future<void> _mediaAct(String command) async {
    try {
      await _state!.call('media.control', {'command': command});
    } catch (_) {}
    await _readMedia();
  }

  @override
  Widget build(BuildContext context) {
    final s = AppScope.of(context);
    final bar = s.darkScheme;
    final theme = buildTheme(bar);
    final autoHide = s.setting<bool>('TopBarAutoHide', false);
    return Theme(
      data: theme,
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
              return Transform.translate(offset: Offset(0, -(barHeight + barTopInset) * e), child: child);
            },
            child: Padding(
              padding: const EdgeInsets.fromLTRB(barSideInset, barTopInset, barSideInset, barBottomInset),
              // No backing strip: only the islands hang over the desktop.
              child: SizedBox(
                height: barHeight,
                child: _content(context, s, bar, autoHide),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _content(BuildContext context, AppState s, ColorScheme bar, bool autoHide) {
    final showMedia = s.setting<bool>('TopBarMediaEnabled', true);
    final showTray = s.setting<bool>('TopBarTrayEnabled', true);
    final showSearch = s.setting<bool>('TopBarSearchEnabled', true);
    final showDate = s.setting<bool>('TopBarShowDate', false);
    final name = s.foregroundName == null ? tr('Desktop') : tr(s.foregroundName!);
    final power = _battery;
    final left = Row(mainAxisSize: MainAxisSize.min, children: [
      const SizedBox(width: 5),
      _Island(bar: bar, children: [
        _BarButton(kind: 'main', model: m, bar: bar, tooltip: 'FloatingDock menu', child: Container(width: 12, height: 12, decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: bar.onSurface, width: 2)))),
        ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 360),
          child: Padding(
            padding: const EdgeInsets.only(right: 12, left: 2),
            child: Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: bar.onSurface)),
          ),
        ),
      ]),
      if (showMedia && _media != null) ...[const SizedBox(width: 8), _mediaCapsule(bar, _media!)],
    ]);
    final right = Row(mainAxisSize: MainAxisSize.min, children: [
      if (showSearch || showMedia || showTray)
        _Island(bar: bar, children: [
          if (showSearch) _BarButton(model: m, bar: bar, tooltip: 'Search', icon: Icons.search_rounded, onTap: () => m.onSearch?.call()),
          if (showMedia) _BarButton(kind: 'media', model: m, bar: bar, tooltip: 'Media', icon: Icons.music_note_rounded),
          if (showTray) _BarButton(kind: 'tray', model: m, bar: bar, tooltip: 'System tray', icon: Icons.expand_more_rounded, iconSize: 18),
        ]),
      const SizedBox(width: 6),
      _Island(bar: bar, children: [
        if (power != null && power['present'] == true)
          _BarButton(
            model: m,
            bar: bar,
            tooltip: 'Battery settings',
            onTap: () => m.onOpenUri?.call('ms-settings:batterysaver'),
            child: Text('${power['charging'] == true ? '↯ ' : ''}${power['percent']}%', style: TextStyle(fontSize: 12, color: bar.onSurface)),
          ),
        _BarButton(kind: 'network', model: m, bar: bar, tooltip: 'Network', child: Opacity(opacity: _online ? 1 : .4, child: Icon(Icons.wifi_rounded, size: 17, color: bar.onSurface))),
        _BarButton(kind: 'sound', model: m, bar: bar, tooltip: 'Sound', icon: Icons.volume_up_rounded),
        _BarButton(kind: 'language', model: m, bar: bar, tooltip: 'Input language', child: Text(_language.isEmpty ? '—' : _language, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: bar.onSurface))),
      ]),
      const SizedBox(width: 6),
      _Island(bar: bar, children: [
        _BarButton(model: m, bar: bar, tooltip: 'Windows settings', icon: Icons.settings_rounded, onTap: () => m.onOpenUri?.call('ms-settings:')),
        _BarButton(
          kind: 'calendar',
          model: m,
          bar: bar,
          tooltip: 'Date and time',
          padding: const EdgeInsets.only(left: 8, right: 2),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            if (showDate) ...[Text(_date, style: TextStyle(fontSize: 12, color: bar.onSurfaceVariant)), const SizedBox(width: 6)],
            Container(
              height: 24,
              padding: const EdgeInsets.symmetric(horizontal: 9),
              alignment: Alignment.center,
              decoration: BoxDecoration(color: bar.primaryContainer, borderRadius: BorderRadius.circular(12)),
              child: Text(_time, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: bar.onPrimaryContainer)),
            ),
          ]),
        ),
      ]),
      const SizedBox(width: 5),
    ]);
    final center = m.workspaces > 0 ? _workspaces(bar) : const SizedBox.shrink();
    return Stack(alignment: Alignment.center, children: [
      Align(alignment: Alignment.centerLeft, child: left),
      Align(alignment: Alignment.center, child: center),
      Align(alignment: Alignment.centerRight, child: right),
    ]);
  }

  Widget _workspaces(ColorScheme bar) => Row(mainAxisSize: MainAxisSize.min, children: [
        _Island(bar: bar, children: [
          for (var i = 1; i <= m.workspaces; i++)
            _BarButton(
              model: m,
              bar: bar,
              tooltip: trf('Workspace {0}', [i]),
              onTap: () => m.onWorkspace?.call(i),
              fill: i == m.activeWorkspace ? bar.primary : null,
              child: Text('$i', style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: i == m.activeWorkspace ? bar.onPrimary : bar.onSurfaceVariant)),
            ),
        ]),
        Padding(
          padding: const EdgeInsets.only(left: 8),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(tr('Desktop'), style: TextStyle(fontSize: 10, color: bar.onSurfaceVariant, height: 1.1)),
            Text(trf('Workspace {0}', [m.activeWorkspace]), style: TextStyle(fontSize: 13, fontWeight: FontWeight.w500, color: bar.onSurface, height: 1.1)),
          ]),
        ),
      ]);

  Widget _mediaCapsule(ColorScheme bar, _MediaInfo info) => _Island(bar: bar, children: [
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
            constraints: const BoxConstraints(maxWidth: 170),
            child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(info.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: bar.onSurface, height: 1.1)),
              if (info.artist.isNotEmpty) Text(info.artist, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 10, color: bar.onSurfaceVariant, height: 1.1)),
            ]),
          ),
        ),
        _BarButton(model: m, bar: bar, tooltip: 'Play / pause', icon: info.playing ? Icons.pause_rounded : Icons.play_arrow_rounded, iconSize: 16, width: 26, fill: bar.primary, iconColor: bar.onPrimary, enabled: info.canToggle, onTap: () => _mediaAct('toggle')),
        _BarButton(model: m, bar: bar, tooltip: 'Next', icon: Icons.skip_next_rounded, iconSize: 16, width: 26, enabled: info.canNext, onTap: () => _mediaAct('next')),
      ]);
}

/// A rounded tonal island that holds a few related buttons.
class _Island extends StatelessWidget {
  const _Island({required this.bar, required this.children});
  final ColorScheme bar;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Container(
        height: 30,
        padding: const EdgeInsets.symmetric(horizontal: 2),
        decoration: BoxDecoration(color: bar.surfaceContainerHigh.withValues(alpha: .86), borderRadius: BorderRadius.circular(15)),
        child: Row(mainAxisSize: MainAxisSize.min, children: [for (var i = 0; i < children.length; i++) ...[if (i > 0) const SizedBox(width: 2), children[i]]]),
      );
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
          decoration: BoxDecoration(
            color: Color.alphaBlend(bar.onSurface.withValues(alpha: layer), widget.fill ?? Colors.transparent),
            borderRadius: BorderRadius.circular(13),
          ),
          child: widget.child ?? Icon(widget.icon, size: widget.iconSize, color: fg),
        ),
      ),
    );
  }
}
