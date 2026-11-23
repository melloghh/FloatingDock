import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../core/app_state.dart';
import '../../core/theme.dart';
import '../../ui/app_icon.dart';
import '../../ui/motion.dart';
import 'dock_math.dart';

const double previewCardWidth = 232, previewHeaderHeight = 40, previewThumbMax = 150, previewGap = 10, previewPad = 12;

/// One live preview card's layout: where DWM draws the window and how big the card is.
class PreviewLayout {
  PreviewLayout(this.window, this.thumbSize, {this.snapshot});
  final DockWindowInfo window;
  final Size thumbSize;

  /// A minimized window cannot be drawn live: this is what it showed the last time the engine saw it (PNG), if it ever did.
  final Uint8List? snapshot;
}

/// What the controller tells the preview window.
class PreviewModel extends ChangeNotifier {
  final visible = ValueNotifier<bool>(false);
  String appName = '';
  Map<String, Object?>? app;
  List<PreviewLayout> cards = const [];
  int revision = 0;

  /// The dock's edge: the panel opens on the side facing away from it.
  String edge = 'Bottom';

  /// Transparent margin around the panel (room for the pop overshoot and the shadow); the side that faces the dock gets less,
  /// so the window never covers the dock's icons and takes the pointer away from them.
  static const double margin = 28, faceMargin = 14;
  EdgeInsets get insets => switch (edge) {
        'Left' => const EdgeInsets.fromLTRB(faceMargin, margin, margin, margin),
        'Right' => const EdgeInsets.fromLTRB(margin, margin, faceMargin, margin),
        _ => const EdgeInsets.fromLTRB(margin, margin, margin, faceMargin),
      };
  Alignment get anchor => switch (edge) { 'Left' => Alignment.centerLeft, 'Right' => Alignment.centerRight, _ => Alignment.bottomCenter };

  /// The native window's logical size for a panel of [panel].
  Size windowSizeFor(Size panel) => Size(panel.width + insets.horizontal, panel.height + insets.vertical);

  void Function(DockWindowInfo window)? onActivate, onClose;
  void Function(bool inside)? onPointer;

  /// Called with the rectangles (logical, window-relative) where DWM should draw the thumbnails, once the surface has settled.
  void Function(List<(DockWindowInfo, Rect)> rects)? onThumbnails;

  /// How opaque DWM draws the live pictures right now: they fade in once the surface has settled instead of snapping in.
  double thumbOpacity = 1;

  /// The size of the panel for [cards] cards whose pictures are [thumbHeight] tall (including padding and the 1 px border).
  static Size panelSize(int cards, double thumbHeight) =>
      Size(cards * previewCardWidth + (cards - 1) * previewGap + 2 * previewPad + 2, previewHeaderHeight + thumbHeight + 8 + 2 * previewPad + 2);

  void open({required String appName, required Map<String, Object?>? app, required List<PreviewLayout> cards}) {
    this.appName = appName;
    this.app = app;
    this.cards = cards;
    revision++;
    notifyListeners();
  }
}

/// The window previews of one app: a row of cards, each with the window title, a close button and a live picture.
/// The pictures are drawn by DWM over this window once the pop-in has finished.
class PreviewView extends StatefulWidget {
  const PreviewView({super.key, required this.model});
  final PreviewModel model;

  @override
  State<PreviewView> createState() => _PreviewViewState();
}

class _PreviewViewState extends State<PreviewView> {
  final _thumbKeys = <int, GlobalKey>{};
  Timer? _settle, _fade;
  PreviewModel get m => widget.model;

  @override
  void initState() {
    super.initState();
    m.addListener(_onChanged);
    m.visible.addListener(_onChanged);
  }

  @override
  void dispose() {
    _settle?.cancel();
    _fade?.cancel();
    m.removeListener(_onChanged);
    m.visible.removeListener(_onChanged);
    super.dispose();
  }

  void _onChanged() {
    setState(() {});
    _settle?.cancel();
    _fade?.cancel();
    if (!m.visible.value) return;
    // The surface scales while it pops in; the live pictures are placed after it has settled.
    _settle = Timer(const Duration(milliseconds: 270), _reportRects);
  }

  void _reportRects() {
    if (!mounted || !m.visible.value) return;
    final rects = <(DockWindowInfo, Rect)>[];
    for (final c in m.cards) {
      if (c.window.minimized) continue;       // DWM has nothing live to draw for a minimized window: the card shows its icon
      final box = _thumbKeys[c.window.hwnd]?.currentContext?.findRenderObject() as RenderBox?;
      if (box == null || !box.attached) continue;
      rects.add((c.window, box.localToGlobal(Offset.zero) & box.size));
    }
    // The pictures fade in over a few frames.
    var step = 1;
    const steps = 4;
    m.thumbOpacity = step / steps;
    m.onThumbnails?.call(rects);
    _fade = Timer.periodic(const Duration(milliseconds: 30), (timer) {
      step++;
      m.thumbOpacity = step / steps;
      m.onThumbnails?.call(rects);
      if (step >= steps) timer.cancel();
    });
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Material(
      type: MaterialType.transparency,
      child: Padding(
        padding: m.insets,
        child: Align(
          alignment: m.anchor,
          child: ValueListenableBuilder<bool>(
            valueListenable: m.visible,
            builder: (context, visible, child) => PopSurface(visible: visible, origin: m.anchor, milliseconds: 260, child: child!),
            child: MouseRegion(
              onEnter: (_) => m.onPointer?.call(true),
              onExit: (_) => m.onPointer?.call(false),
              child: Container(
                padding: const EdgeInsets.all(previewPad),
                decoration: BoxDecoration(
                  color: cs.surfaceContainerHigh,
                  borderRadius: BorderRadius.circular(Shapes.xl),
                  border: Border.all(color: cs.outlineVariant.withValues(alpha: .35)),
                  boxShadow: const [BoxShadow(color: Color(0x2E000000), blurRadius: 28, offset: Offset(0, 6))],
                ),
                child: Row(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
                  for (var i = 0; i < m.cards.length; i++) ...[
                    if (i > 0) const SizedBox(width: previewGap),
                    _Card(
                      layout: m.cards[i],
                      app: m.app,
                      thumbKey: _thumbKeys.putIfAbsent(m.cards[i].window.hwnd, GlobalKey.new),
                      onActivate: () => m.onActivate?.call(m.cards[i].window),
                      onClose: () => m.onClose?.call(m.cards[i].window),
                    ),
                  ],
                ]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _Card extends StatefulWidget {
  const _Card({required this.layout, required this.app, required this.thumbKey, required this.onActivate, required this.onClose});
  final PreviewLayout layout;
  final Map<String, Object?>? app;
  final GlobalKey thumbKey;
  final VoidCallback onActivate, onClose;

  @override
  State<_Card> createState() => _CardState();
}

class _CardState extends State<_Card> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final w = widget.layout.window;
    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: widget.onActivate,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 140),
          width: previewCardWidth,
          decoration: BoxDecoration(
            color: _hover ? cs.secondaryContainer : cs.surfaceContainerHighest.withValues(alpha: .6),
            borderRadius: BorderRadius.circular(Shapes.l),
          ),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            SizedBox(
              height: previewHeaderHeight,
              child: Padding(
                padding: const EdgeInsets.only(left: 10, right: 4),
                child: Row(children: [
                  if (widget.app != null) AppIcon(app: widget.app!, size: 18),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(w.title.isEmpty ? '—' : w.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12.5, color: _hover ? cs.onSecondaryContainer : cs.onSurface)),
                  ),
                  SizedBox(
                    width: 28,
                    height: 28,
                    child: IconButton(
                      padding: EdgeInsets.zero,
                      iconSize: 16,
                      tooltip: tr('Close window'),
                      color: cs.onSurfaceVariant,
                      icon: const Icon(Icons.close_rounded),
                      onPressed: widget.onClose,
                    ),
                  ),
                ]),
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(8, 0, 8, 8),
              child: Center(
                child: SizedBox(
                  key: widget.thumbKey,
                  width: widget.layout.thumbSize.width,
                  height: widget.layout.thumbSize.height,
                  child: DecoratedBox(
                    decoration: BoxDecoration(color: cs.surfaceContainerLowest.withValues(alpha: .5), borderRadius: BorderRadius.circular(Shapes.s)),
                    child: !w.minimized
                        ? null
                        : widget.layout.snapshot != null
                            ? ClipRRect(
                                borderRadius: BorderRadius.circular(Shapes.s),
                                child: Opacity(
                                  opacity: .8,
                                  child: Image.memory(widget.layout.snapshot!, fit: BoxFit.cover, gaplessPlayback: true, filterQuality: FilterQuality.medium),
                                ),
                              )
                            : widget.app != null
                                ? Center(child: Opacity(opacity: .85, child: AppIcon(app: widget.app!, size: 40)))
                                : null,
                  ),
                ),
              ),
            ),
          ]),
        ),
      ),
    );
  }
}

/// Fits a source window of [pixels] size into the card's picture area, keeping its aspect ratio.
Size thumbSizeFor(Size? pixels) {
  const maxW = previewCardWidth - 16, maxH = previewThumbMax;
  if (pixels == null || pixels.width < 1 || pixels.height < 1) return const Size(maxW, maxH * .75);
  final scale = math.min(maxW / pixels.width, maxH / pixels.height);
  return Size((pixels.width * scale).floorToDouble().clamp(60, maxW), (pixels.height * scale).floorToDouble().clamp(40, maxH));
}
