import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/app_state.dart';
import '../../ui/app_icon.dart';
import '../../ui/motion.dart';

/// What the controller tells the search view about the window it sits in.
class SearchModel extends ChangeNotifier {
  /// Pop state of the surface; false while the window is hidden.
  final visible = ValueNotifier<bool>(false);

  /// Logical width of the panel and the room the window leaves for it (height).
  double panelWidth = 650;
  double maxHeight = 580;

  /// Where the panel's top-left corner is inside the window (logical px): the window has a margin for the pop overshoot and,
  /// when the search grows out of the dock, reaches down to it.
  Offset panelOrigin = const Offset(28, 28);

  /// Set when the search grows out of the dock: where the panel starts, relative to its place (logical px).
  Offset? from;

  /// Set when the search takes the dock's place: the edge ('Bottom', 'Left', 'Right') the panel is flush with. Its ends join the screen's
  /// edge in concave corners, and it grows away from that edge. Null: a floating panel.
  String? dockEdge;

  /// How far the concave corners of the flush panel reach along the screen's edge (logical px).
  static const double fillet = 14;

  /// Increases on every show: the view resets its text and results.
  int shown = 0;

  void Function()? onClose;

  void reopen({required double width, required double height, Offset panelOrigin = const Offset(28, 28), Offset? from, String? dockEdge}) {
    panelWidth = width;
    maxHeight = height;
    this.panelOrigin = panelOrigin;
    this.from = from;
    this.dockEdge = dockEdge;
    shown++;
    notifyListeners();
  }
}

class SearchResultItem {
  SearchResultItem(Map m) : id = '${m['id']}', kind = '${m['kind']}', name = '${m['name']}', app = (m['app'] as Map?)?.cast<String, Object?>();
  final String id, kind, name;
  final Map<String, Object?>? app;
}

/// A keyboard-first palette: a pill for the query and a card of results with one highlight that glides between rows.
class SearchView extends StatefulWidget {
  const SearchView({super.key, required this.model});
  final SearchModel model;

  @override
  State<SearchView> createState() => _SearchViewState();
}

class _SearchViewState extends State<SearchView> {
  final _text = TextEditingController();
  final _focus = FocusNode();
  final _scroll = ScrollController();
  Timer? _debounce;
  List<SearchResultItem> _results = const [];
  List<SearchResultItem> _idleResults = const [];
  String _idleStatus = 'idle';
  String _status = 'idle';
  int _selected = -1;
  int _generation = 0;
  DateTime _openedAt = DateTime.now();
  bool _pointerSelects = false;

  static const _rowExtent = 46.0, _rowGap = 2.0;

  @override
  void initState() {
    super.initState();
    widget.model.addListener(_onShown);
    // Warm the empty-query list before the first open.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _query();
    });
  }

  @override
  void dispose() {
    widget.model.removeListener(_onShown);
    _debounce?.cancel();
    _text.dispose();
    _focus.dispose();
    _scroll.dispose();
    super.dispose();
  }

  void _onShown() {
    _openedAt = DateTime.now();
    _pointerSelects = false;
    _text.clear();
    // The list for an empty query is kept from the last time, so it is there with the very first frame of the pop.
    setState(() {
      _results = _idleResults;
      _status = _idleStatus;
      _selected = _idleResults.isEmpty ? -1 : 0;
    });
    _generation++;
    WidgetsBinding.instance.addPostFrameCallback((_) => _focus.requestFocus());
    _query();
  }

  void _onChanged(String _) {
    _generation++;
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 180), _query);
  }

  Future<void> _query() async {
    final gen = ++_generation;
    final state = AppScope.read(context);
    try {
      final r = await state.call('search.query', {'text': _text.text}) as Map;
      if (!mounted || gen != _generation) return;
      final items = [for (final m in (r['results'] as List).cast<Map>()) SearchResultItem(m)];
      if (_text.text.isEmpty) {
        _idleResults = items;
        _idleStatus = '${r['status']}';
      }
      setState(() {
        _results = items;
        _status = '${r['status']}';
        _selected = items.isEmpty ? -1 : 0;
      });
      if (_scroll.hasClients) _scroll.jumpTo(0);
    } catch (_) {
      if (mounted && gen == _generation) setState(() => _status = 'error');
    }
  }

  void _select(int index) {
    if (index == _selected || index < 0 || index >= _results.length) return;
    setState(() => _selected = index);
    _reveal(index);
  }

  void _reveal(int index) {
    if (!_scroll.hasClients) return;
    final top = index * (_rowExtent + _rowGap);
    final view = _scroll.position.viewportDimension;
    if (top < _scroll.offset) {
      _scroll.animateTo(top, duration: const Duration(milliseconds: 160), curve: Curves.easeOut);
    } else if (top + _rowExtent > _scroll.offset + view) {
      _scroll.animateTo(top + _rowExtent - view, duration: const Duration(milliseconds: 160), curve: Curves.easeOut);
    }
  }

  void _open(SearchResultItem item) {
    final state = AppScope.read(context);
    widget.model.onClose?.call();
    unawaited(state.call('search.open', {'id': item.id}));
  }

  KeyEventResult _onKey(FocusNode node, KeyEvent event) {
    if (event is KeyUpEvent) return KeyEventResult.ignored;
    final key = event.logicalKey;
    if (key == LogicalKeyboardKey.escape) {
      widget.model.onClose?.call();
      return KeyEventResult.handled;
    }
    if (key == LogicalKeyboardKey.arrowDown || key == LogicalKeyboardKey.arrowUp) {
      if (_results.isNotEmpty) _select((_selected + (key == LogicalKeyboardKey.arrowDown ? 1 : -1)).clamp(0, _results.length - 1));
      return KeyEventResult.handled;
    }
    if (key == LogicalKeyboardKey.enter || key == LogicalKeyboardKey.numpadEnter) {
      if (_selected >= 0 && _selected < _results.length) _open(_results[_selected]);
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }

  String get _statusText {
    switch (_status) {
      case 'idle':
        return tr('Search apps');
      case 'none':
        return tr('No matches. Try an app name.');
      case 'error':
        return tr('Search could not finish. Try again.');
      default:
        return tr('↑ ↓ to select · Enter to open · Esc to close');
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final m = widget.model;
    final listMax = (m.maxHeight - 96).clamp(44.0, 460.0);
    final listHeight = (_results.length * (_rowExtent + _rowGap) - _rowGap).clamp(0.0, listMax);
    final showStatus = _results.isEmpty && _status != 'idle';
    final edge = m.dockEdge;
    final content = SizedBox(
      width: m.panelWidth,
      // The results above, the query in an inset field at the bottom.
      child: Padding(
        padding: const EdgeInsets.all(8),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (_results.isNotEmpty) ...[
              SizedBox(
                height: listHeight,
                child: MouseRegion(
                  onHover: (_) => _pointerSelects = DateTime.now().difference(_openedAt) > const Duration(milliseconds: 300),
                  child: SingleChildScrollView(
                    controller: _scroll,
                    child: GlideList(
                      count: _results.length,
                      selected: _selected < 0 ? null : _selected,
                      itemExtent: _rowExtent,
                      gap: _rowGap,
                      radius: 12,
                      highlight: cs.primary,
                      itemBuilder: (context, i, selected) => _ResultRow(
                        item: _results[i],
                        selected: selected,
                        onHover: () {
                          if (_pointerSelects) _select(i);
                        },
                        onTap: () => _open(_results[i]),
                      ),
                    ),
                  ),
                ),
              ),
              const SizedBox(height: 8),
            ],
            if (showStatus)
              Padding(
                padding: const EdgeInsets.fromLTRB(12, 4, 12, 12),
                child: Text(_statusText, style: TextStyle(color: cs.onSurfaceVariant, fontSize: 12)),
              ),
            _InputField(controller: _text, focus: _focus, onChanged: _onChanged, onKey: _onKey),
          ],
        ),
      ),
    );
    final fill = cs.surfaceContainerHigh;
    final Widget panel = edge == null
        ? DecoratedBox(
            decoration: BoxDecoration(
              color: fill,
              borderRadius: BorderRadius.circular(22),
              border: Border.all(color: cs.outlineVariant.withValues(alpha: .3)),
              boxShadow: const [BoxShadow(color: Color(0x40000000), blurRadius: 30, offset: Offset(0, 8))],
            ),
            child: content,
          )
        : CustomPaint(
            painter: _FlushPanelPainter(edge, fill),
            child: Padding(
              padding: edge == 'Bottom'
                  ? const EdgeInsets.symmetric(horizontal: SearchModel.fillet)
                  : const EdgeInsets.symmetric(vertical: SearchModel.fillet),
              child: content,
            ),
          );
    final (alignment, origin) = switch (edge) {
      'Bottom' => (Alignment.bottomCenter, Alignment.bottomCenter),
      'Left' => (Alignment.centerLeft, Alignment.centerLeft),
      'Right' => (Alignment.centerRight, Alignment.centerRight),
      _ => (Alignment.topLeft, Alignment.topCenter),
    };
    return GestureDetector(
      behavior: HitTestBehavior.translucent,
      onTap: () => m.onClose?.call(), // a click in the transparent margin dismisses, like a click anywhere outside
      child: Padding(
        padding: edge == null ? EdgeInsets.only(left: m.panelOrigin.dx, top: m.panelOrigin.dy) : EdgeInsets.zero,
        child: Align(
          alignment: alignment,
          child: ValueListenableBuilder<bool>(
            valueListenable: m.visible,
            builder: (context, visible, child) => m.from == null
                ? PopSurface(visible: visible, child: child!)
                : PopSurface(visible: visible, from: m.from, origin: origin, startScale: .32, milliseconds: 520, child: child!),
            child: GestureDetector(
              onTap: () {},
              child: Material(type: MaterialType.transparency, child: panel),
            ),
          ),
        ),
      ),
    );
  }
}

/// The panel that has taken the dock's place: flush with one screen edge, its far corners rounded and, where it meets the edge,
/// a concave corner on each side that joins it to the screen. Drawn for the bottom edge and turned for the others.
class _FlushPanelPainter extends CustomPainter {
  const _FlushPanelPainter(this.edge, this.color);
  final String edge;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    // Work in the "bottom edge" frame: [a] along the edge, [b] away from it.
    final vertical = edge != 'Bottom';
    final a = vertical ? size.height : size.width, b = vertical ? size.width : size.height;
    if (edge == 'Left') {
      canvas.translate(b, 0);
      canvas.rotate(math.pi / 2);
    } else if (edge == 'Right') {
      canvas.translate(0, a);
      canvas.rotate(-math.pi / 2);
    }
    const r = SearchModel.fillet, corner = 22.0;
    final path = Path()
      ..addRRect(
        RRect.fromRectAndCorners(Rect.fromLTRB(r, 0, a - r, b), topLeft: const Radius.circular(corner), topRight: const Radius.circular(corner)),
      )
      ..moveTo(0, b)
      ..lineTo(r, b)
      ..lineTo(r, b - r)
      ..arcTo(Rect.fromCircle(center: Offset(0, b - r), radius: r), 0, math.pi / 2, false)
      ..close()
      ..moveTo(a, b)
      ..lineTo(a - r, b)
      ..lineTo(a - r, b - r)
      ..arcTo(Rect.fromCircle(center: Offset(a, b - r), radius: r), math.pi, -math.pi / 2, false)
      ..close();
    canvas.drawPath(
      path,
      Paint()
        ..color = color
        ..isAntiAlias = true,
    );
  }

  @override
  bool shouldRepaint(_FlushPanelPainter old) => old.edge != edge || old.color != color;
}

class _InputField extends StatelessWidget {
  const _InputField({required this.controller, required this.focus, required this.onChanged, required this.onKey});
  final TextEditingController controller;
  final FocusNode focus;
  final ValueChanged<String> onChanged;
  final FocusOnKeyEventCallback onKey;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return DecoratedBox(
      decoration: BoxDecoration(color: cs.surfaceContainerHighest, borderRadius: BorderRadius.circular(12)),
      child: SizedBox(
        height: 44,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 14),
          child: Focus(
            onKeyEvent: onKey,
            child: TextField(
              controller: controller,
              focusNode: focus,
              onChanged: onChanged,
              style: TextStyle(fontSize: 14, color: cs.onSurface),
              cursorColor: cs.onSurface,
              decoration: InputDecoration(
                hintText: tr('Search apps'),
                hintStyle: TextStyle(color: cs.onSurfaceVariant.withValues(alpha: .7)),
                border: InputBorder.none,
                enabledBorder: InputBorder.none,
                focusedBorder: InputBorder.none,
                filled: false,
                isCollapsed: true,
                contentPadding: const EdgeInsets.symmetric(vertical: 14),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _ResultRow extends StatelessWidget {
  const _ResultRow({required this.item, required this.selected, required this.onTap, required this.onHover});
  final SearchResultItem item;
  final bool selected;
  final VoidCallback onTap, onHover;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final fg = selected ? cs.onPrimary : cs.onSurface;
    final sub = selected ? cs.onPrimary.withValues(alpha: .7) : cs.onSurfaceVariant;
    final isApp = item.app != null;
    return MouseRegion(
      onHover: (_) => onHover(),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 10),
          child: Row(
            children: [
              SizedBox(
                width: 32,
                height: 32,
                child: isApp
                    ? Center(child: AppIcon(app: item.app!, size: 28))
                    : DecoratedBox(
                        decoration: BoxDecoration(color: cs.surfaceContainerHighest, borderRadius: BorderRadius.circular(8)),
                        child: Icon(item.id == 'cmd:launchpad' ? Icons.apps_rounded : Icons.settings_rounded, size: 18, color: cs.onSurface),
                      ),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      isApp ? item.name : tr(item.name),
                      style: TextStyle(fontSize: 13.5, fontWeight: FontWeight.w600, color: fg, height: 1.2),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    Text(
                      tr(item.kind),
                      style: TextStyle(fontSize: 11, color: sub, height: 1.2),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
