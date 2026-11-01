import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/app_state.dart';
import '../../core/theme.dart';
import '../../ui/app_icon.dart';
import '../../ui/motion.dart';
import '../../ui/m3.dart';
import '../popup/context_menu.dart';

/// What the controller tells the Launchpad about its window.
class LaunchpadModel extends ChangeNotifier {
  final visible = ValueNotifier<bool>(false);

  /// Logical size of the monitor the overlay covers.
  Size screen = const Size(1920, 1080);

  /// Increases on every show: the view reloads the apps and replays its opening.
  int shown = 0;

  void Function()? onClose;
  Future<void> Function(List<ContextItem> items, Offset globalPosition)? onContextMenu;
  Future<String?> Function(List<(String, String)> filters)? pickFile;

  /// Paths dropped on the window (from Explorer).
  final dropped = StreamController<List<String>>.broadcast();

  void reopen(Size screenSize) {
    screen = screenSize;
    shown++;
    notifyListeners();
  }
}

/// The Launchpad: a scrim over the screen and one rounded surface with every app as a tile.
class LaunchpadView extends StatefulWidget {
  const LaunchpadView({super.key, required this.model});
  final LaunchpadModel model;

  @override
  State<LaunchpadView> createState() => _LaunchpadViewState();
}

class _LaunchpadViewState extends State<LaunchpadView> {
  List<Map<String, Object?>> _apps = const [];
  bool _loaded = false, _loading = false;
  String? _toast;
  Timer? _toastTimer;
  StreamSubscription<dynamic>? _tilesSub, _dropSub;
  final _focus = FocusNode();
  DateTime _openedAt = DateTime.now();
  int _generation = 0;

  @override
  void initState() {
    super.initState();
    widget.model.addListener(_onShown);
    _dropSub = widget.model.dropped.stream.listen(_onDrop);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _tilesSub ??= AppScope.read(context).eventsOther.listen((e) {
      if (e.name == 'launchpad.changed' && widget.model.visible.value) _load();
    });
  }

  @override
  void dispose() {
    widget.model.removeListener(_onShown);
    _tilesSub?.cancel();
    _dropSub?.cancel();
    _toastTimer?.cancel();
    _focus.dispose();
    super.dispose();
  }

  void _onShown() {
    _openedAt = DateTime.now();
    _generation++;
    WidgetsBinding.instance.addPostFrameCallback((_) => _focus.requestFocus());
    _load();
  }

  Future<void> _load({bool refresh = false}) async {
    if (_loading) return;
    _loading = true;
    final state = AppScope.read(context);
    try {
      final r = await state.call('launchpad.apps', {'refresh': refresh}) as Map;
      if (mounted) {
        setState(() {
          _apps = (r['apps'] as List).cast<Map>().map((m) => m.cast<String, Object?>()).toList();
          _loaded = true;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loaded = true);
    } finally {
      _loading = false;
    }
  }

  void _say(String text) {
    _toastTimer?.cancel();
    setState(() => _toast = text);
    _toastTimer = Timer(const Duration(seconds: 3), () {
      if (mounted) setState(() => _toast = null);
    });
  }

  Future<void> _onDrop(List<String> paths) async {
    final state = AppScope.read(context);
    var added = 0, duplicate = 0, unsupported = 0, failed = 0;
    for (final path in paths) {
      final r = await state.call('tiles.add', {'path': path}) as Map?;
      switch (r?['status']) {
        case 'Added':
          added++;
        case 'Duplicate':
          duplicate++;
        case 'SaveFailed':
          failed++;
        default:
          unsupported++;
      }
    }
    final parts = [
      if (added > 0) '${tr('Added')} $added',
      if (duplicate > 0) '$duplicate ${tr('already in Launchpad')}',
      if (unsupported > 0) '$unsupported ${tr('not supported (use .lnk or .exe)')}',
      if (failed > 0) trf('{0} could not be saved', [failed]),
    ];
    _say(parts.isEmpty ? tr('Nothing to add') : parts.join(' · '));
  }

  Future<void> _addApp() async {
    final state = AppScope.read(context);
    final path = await widget.model.pickFile?.call(const [('Applications and shortcuts', '*.exe;*.lnk')]);
    if (path == null) return;
    final r = await state.call('tiles.add', {'path': path}) as Map?;
    if (r != null && r['status'] != 'Added') {
      _say(tr(r['status'] == 'Duplicate' ? 'Already added.' : r['status'] == 'SaveFailed' ? 'Could not add application.' : 'This file type is not supported.'));
    }
  }

  void _launch(Map<String, Object?> app) {
    final state = AppScope.read(context);
    widget.model.onClose?.call();
    state.call('apps.launch', {'app': app});
    state.act('noteLaunch');
  }

  Future<void> _tileMenu(Map<String, Object?> app, Offset at) async {
    final state = AppScope.read(context);
    final info = await state.call('apps.info', {'app': app}) as Map;
    final pinned = info['pinned'] == true, isTile = info['isTile'] == true;
    final index = (info['tileIndex'] as num).toInt(), count = (info['tileCount'] as num).toInt();
    final items = [
      ContextItem('Open', icon: Icons.open_in_new_rounded, onTap: () => _launch(app)),
      ContextItem(pinned ? 'Unpin from Dock' : 'Pin to Dock',
          icon: pinned ? Icons.push_pin_outlined : Icons.push_pin_rounded,
          onTap: () => state.call(pinned ? 'pinned.remove' : 'pinned.addApp', pinned ? {'key': app['Key']} : {'app': app})),
      if (isTile) ...[
        ContextItem('Remove from Launchpad', icon: Icons.delete_outline_rounded, onTap: () => state.call('tiles.remove', {'key': app['Key']})),
        ContextItem('Move earlier', icon: Icons.arrow_back_rounded, enabled: index > 0, onTap: () => state.call('tiles.move', {'key': app['Key'], 'index': index - 1})),
        ContextItem('Move later', icon: Icons.arrow_forward_rounded, enabled: index < count - 1, onTap: () => state.call('tiles.move', {'key': app['Key'], 'index': index + 1})),
      ],
      const ContextItem.separator(),
      ContextItem('Change icon…', icon: Icons.image_outlined, onTap: () async {
        final path = await widget.model.pickFile?.call(const [('Images', '*.png;*.ico')]);
        if (path != null) await state.call('icons.override', {'key': app['Key'], 'path': path});
      }),
      ContextItem('Reset icon', icon: Icons.restart_alt_rounded, enabled: info['hasOverride'] == true, onTap: () => state.call('icons.reset', {'key': app['Key']})),
      ContextItem('Open file location', icon: Icons.folder_open_rounded, enabled: info['hasLocation'] == true, onTap: () {
        state.call('apps.reveal', {'app': app});
        widget.model.onClose?.call();
      }),
    ];
    await widget.model.onContextMenu?.call(items, at);
  }

  Future<void> _power() async {
    final state = AppScope.read(context);
    final box = context.findRenderObject() as RenderBox;
    final items = [
      ContextItem('Shut down', icon: Icons.power_settings_new_rounded, onTap: () => _confirmPower(state, false)),
      ContextItem('Restart', icon: Icons.restart_alt_rounded, onTap: () => _confirmPower(state, true)),
    ];
    final at = box.localToGlobal(Offset(box.size.width - 100, 120));
    await widget.model.onContextMenu?.call(items, at);
  }

  Future<void> _confirmPower(AppState state, bool restart) async {
    if (!mounted) return;
    final ok = await confirmDialog(context, restart ? 'Restart this PC?' : 'Shut down this PC?', 'Save your work before continuing.', ok: restart ? 'Restart' : 'Shut down', danger: true);
    if (ok) await state.call('power.run', {'action': restart ? 'restart' : 'shutdown'});
  }

  KeyEventResult _onKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent) return KeyEventResult.ignored;
    if (event.logicalKey == LogicalKeyboardKey.escape) {
      widget.model.onClose?.call();
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.enter && _apps.isNotEmpty) {
      _launch(_apps.first);
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }

  @override
  Widget build(BuildContext context) {
    final s = AppScope.of(context);
    final cs = Theme.of(context).colorScheme;
    final m = widget.model;
    final iconSize = s.setting<int>('LaunchpadIconSize', 72).toDouble();
    final spacing = s.setting<int>('LaunchpadSpacing', 28).toDouble();
    final tileWidth = (iconSize + 28).clamp(96.0, 400.0);
    final shellW = (m.screen.width - 48).clamp(320.0, 1440.0);
    final shellH = (m.screen.height - 72).clamp(260.0, 940.0);
    return Material(
      type: MaterialType.transparency,
      child: Focus(
      focusNode: _focus,
      onKeyEvent: _onKey,
      child: ValueListenableBuilder<bool>(
        valueListenable: m.visible,
        builder: (context, visible, _) {
          return _Overlay(
            visible: visible,
            child: GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTap: () {
                // A click that opened the overlay must not close it again.
                if (DateTime.now().difference(_openedAt) > const Duration(milliseconds: 250)) m.onClose?.call();
              },
              child: ColoredBox(
                color: Color.fromARGB(cs.brightness == Brightness.dark ? 64 : 44, 8, 16, 24),
                child: Stack(children: [
                  Center(
                    child: GestureDetector(
                      onTap: () {},
                      child: Container(
                        width: shellW,
                        height: shellH,
                        padding: const EdgeInsets.all(28),
                        decoration: BoxDecoration(
                          color: cs.surfaceContainerHigh,
                          borderRadius: BorderRadius.circular(Shapes.xl),
                          boxShadow: const [BoxShadow(color: Color(0x40000000), blurRadius: 40, offset: Offset(0, 12))],
                        ),
                        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                          _Header(onAdd: _addApp, onRefresh: () => _load(refresh: true), onSettings: () {
                            m.onClose?.call();
                            s.act('settings:Launchpad');
                          }, onPower: _power),
                          const SizedBox(height: 16),
                          Expanded(
                            child: !_loaded || _apps.isEmpty
                                ? Center(
                                    child: Text(
                                      tr(!_loaded ? 'Loading Windows Start pins…' : 'Pin apps in Windows Start or add an application'),
                                      style: TextStyle(fontSize: 16, color: cs.onSurfaceVariant),
                                    ),
                                  )
                                : GridView.builder(
                                    padding: const EdgeInsets.only(bottom: 40),
                                    gridDelegate: SliverGridDelegateWithMaxCrossAxisExtent(
                                      maxCrossAxisExtent: tileWidth + spacing,
                                      mainAxisExtent: iconSize + 8 + 10 + 38 + 16,
                                      mainAxisSpacing: spacing.clamp(4, 120) / 2,
                                      crossAxisSpacing: spacing.clamp(4, 120) / 2,
                                    ),
                                    itemCount: _apps.length,
                                    itemBuilder: (context, i) => _Tile(
                                      key: ValueKey('${_generation}_${_apps[i]['Key']}'),
                                      app: _apps[i],
                                      iconSize: iconSize,
                                      index: i,
                                      animate: i < 48 && DateTime.now().difference(_openedAt) < const Duration(milliseconds: 900),
                                      onTap: () => _launch(_apps[i]),
                                      onMenu: (at) => _tileMenu(_apps[i], at),
                                    ),
                                  ),
                          ),
                        ]),
                      ),
                    ),
                  ),
                  if (_toast != null)
                    Align(
                      alignment: Alignment.bottomCenter,
                      child: Padding(
                        padding: const EdgeInsets.only(bottom: 36),
                        child: Material(
                          color: cs.inverseSurface,
                          borderRadius: BorderRadius.circular(Shapes.m),
                          child: Padding(
                            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
                            child: Text(_toast!, style: TextStyle(fontSize: 14, color: cs.onInverseSurface)),
                          ),
                        ),
                      ),
                    ),
                ]),
              ),
            ),
          );
        },
      ),
      ),
    );
  }
}

/// The overlay's own opening: it fades in while settling from 1.025 to 1, and leaves a little faster.
class _Overlay extends StatefulWidget {
  const _Overlay({required this.visible, required this.child});
  final bool visible;
  final Widget child;

  @override
  State<_Overlay> createState() => _OverlayState();
}

class _OverlayState extends State<_Overlay> with SingleTickerProviderStateMixin {
  late final AnimationController _c = AnimationController(vsync: this, duration: const Duration(milliseconds: 280), reverseDuration: const Duration(milliseconds: 150));

  @override
  void initState() {
    super.initState();
    if (widget.visible) _c.forward();
  }

  @override
  void didUpdateWidget(_Overlay old) {
    super.didUpdateWidget(old);
    if (widget.visible && !old.visible) _c.forward(from: 0);
    if (!widget.visible && old.visible) _c.reverse();
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
        animation: _c,
        child: widget.child,
        builder: (context, child) {
          final leaving = _c.status == AnimationStatus.reverse;
          final t = emphasized.transform(_c.value);
          final scale = leaving ? 1 + .015 * (1 - _c.value) : 1.025 - .025 * t;
          return Opacity(opacity: _c.value, child: Transform.scale(scale: scale, child: child));
        },
      );
}

class _Header extends StatelessWidget {
  const _Header({required this.onAdd, required this.onRefresh, required this.onSettings, required this.onPower});
  final VoidCallback onAdd, onRefresh, onSettings, onPower;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    Widget button(IconData icon, String tooltip, VoidCallback tap) => Tooltip(
          message: tr(tooltip),
          child: SizedBox(
            width: 44,
            height: 44,
            child: Material(
              color: cs.secondaryContainer,
              shape: const CircleBorder(),
              clipBehavior: Clip.antiAlias,
              child: InkWell(onTap: tap, child: Icon(icon, size: 20, color: cs.onSecondaryContainer)),
            ),
          ),
        );
    return SizedBox(
      height: 64,
      child: Row(children: [
        Expanded(
          child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(tr('Apps'), style: TextStyle(fontSize: 28, fontWeight: FontWeight.w500, color: cs.onSurface)),
            Text(tr('Windows Start pins and your apps'), style: TextStyle(fontSize: 13, color: cs.onSurfaceVariant)),
          ]),
        ),
        button(Icons.add_rounded, 'Add application', onAdd),
        const SizedBox(width: 8),
        button(Icons.refresh_rounded, 'Refresh Windows Start pins', onRefresh),
        const SizedBox(width: 8),
        button(Icons.settings_rounded, 'FloatingDock settings', onSettings),
        const SizedBox(width: 8),
        button(Icons.power_settings_new_rounded, 'Power', onPower),
      ]),
    );
  }
}

class _Tile extends StatefulWidget {
  const _Tile({super.key, required this.app, required this.iconSize, required this.index, required this.animate, required this.onTap, required this.onMenu});
  final Map<String, Object?> app;
  final double iconSize;
  final int index;
  final bool animate;
  final VoidCallback onTap;
  final void Function(Offset globalPosition) onMenu;

  @override
  State<_Tile> createState() => _TileState();
}

class _TileState extends State<_Tile> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    Widget tile = MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.onTap,
        onSecondaryTapUp: (d) => widget.onMenu(d.globalPosition),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          margin: const EdgeInsets.all(2),
          padding: const EdgeInsets.fromLTRB(6, 10, 6, 6),
          decoration: BoxDecoration(
            color: _hover ? cs.onSurface.withValues(alpha: .11) : Colors.transparent,
            borderRadius: BorderRadius.circular(Shapes.l),
          ),
          child: Column(children: [
            AnimatedScale(
              scale: _hover ? 1.035 : 1,
              duration: const Duration(milliseconds: 180),
              curve: emphasized,
              child: AppIcon(app: widget.app, size: widget.iconSize),
            ),
            const SizedBox(height: 8),
            Text('${widget.app['Name']}', textAlign: TextAlign.center, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12.5, color: cs.onSurface)),
          ]),
        ),
      ),
    );
    if (widget.animate) tile = Reveal(index: widget.index % 12, distance: 14, blur: 6, milliseconds: 420, child: tile);
    return tile;
  }
}
