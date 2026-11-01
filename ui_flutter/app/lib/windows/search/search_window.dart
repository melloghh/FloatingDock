import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/app_state.dart';
import '../../core/theme.dart';
import '../../ui/app_icon.dart';
import '../../ui/motion.dart';

/// What the controller tells the search view about the window it sits in.
class SearchModel extends ChangeNotifier {
  /// Pop state of the surface; false while the window is hidden.
  final visible = ValueNotifier<bool>(false);

  /// Logical width of the panel and the room the window leaves for it (height).
  double panelWidth = 650;
  double maxHeight = 580;

  /// Increases on every show: the view resets its text and results.
  int shown = 0;

  void Function()? onClose;

  void reopen({required double width, required double height}) {
    panelWidth = width;
    maxHeight = height;
    shown++;
    notifyListeners();
  }
}

class SearchResultItem {
  SearchResultItem(Map m)
      : id = '${m['id']}',
        kind = '${m['kind']}',
        name = '${m['name']}',
        app = (m['app'] as Map?)?.cast<String, Object?>();
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
  String _status = 'idle';
  int _selected = -1;
  int _generation = 0;
  DateTime _openedAt = DateTime.now();
  bool _pointerSelects = false;

  static const _rowExtent = 56.0, _rowGap = 2.0;

  @override
  void initState() {
    super.initState();
    widget.model.addListener(_onShown);
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
    setState(() {
      _results = const [];
      _status = 'idle';
      _selected = -1;
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
    final listMax = (m.maxHeight - 126).clamp(44.0, 440.0);
    final listHeight = (_results.length * (_rowExtent + _rowGap) - _rowGap).clamp(0.0, listMax);
    return GestureDetector(
      behavior: HitTestBehavior.translucent,
      onTap: () => m.onClose?.call(), // a click in the transparent margin dismisses, like a click anywhere outside
      child: Padding(
        padding: const EdgeInsets.all(28),
        child: Align(
          alignment: Alignment.topCenter,
          child: ValueListenableBuilder<bool>(
            valueListenable: m.visible,
            builder: (context, visible, child) => PopSurface(visible: visible, child: child!),
            child: GestureDetector(
              onTap: () {},
              child: SizedBox(
                width: m.panelWidth,
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  _InputPill(controller: _text, focus: _focus, onChanged: _onChanged, onKey: _onKey),
                  const SizedBox(height: 10),
                  Material(
                    color: cs.surfaceContainer,
                    elevation: 0,
                    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(Shapes.xl)),
                    clipBehavior: Clip.antiAlias,
                    child: Padding(
                      padding: const EdgeInsets.all(8),
                      child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                        if (_results.isNotEmpty)
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
                                  highlight: cs.secondaryContainer,
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
                        Padding(
                          padding: const EdgeInsets.fromLTRB(16, 10, 16, 6),
                          child: Text(_statusText, style: TextStyle(color: cs.onSurfaceVariant, fontSize: 12)),
                        ),
                      ]),
                    ),
                  ),
                ]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _InputPill extends StatelessWidget {
  const _InputPill({required this.controller, required this.focus, required this.onChanged, required this.onKey});
  final TextEditingController controller;
  final FocusNode focus;
  final ValueChanged<String> onChanged;
  final FocusOnKeyEventCallback onKey;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Material(
      color: cs.surfaceContainerHigh,
      shape: const StadiumBorder(),
      elevation: 0,
      child: SizedBox(
        height: 56,
        child: Row(children: [
          const SizedBox(width: 20),
          Icon(Icons.search_rounded, size: 24, color: cs.onSurfaceVariant),
          const SizedBox(width: 12),
          Expanded(
            child: Focus(
              onKeyEvent: onKey,
              child: TextField(
                controller: controller,
                focusNode: focus,
                onChanged: onChanged,
                style: TextStyle(fontSize: 16, color: cs.onSurface),
                cursorColor: cs.primary,
                decoration: InputDecoration(
                  hintText: tr('Search apps'),
                  hintStyle: TextStyle(color: cs.onSurfaceVariant.withValues(alpha: .8)),
                  border: InputBorder.none,
                  enabledBorder: InputBorder.none,
                  focusedBorder: InputBorder.none,
                  filled: false,
                  contentPadding: EdgeInsets.zero,
                ),
              ),
            ),
          ),
          Container(
            margin: const EdgeInsets.only(left: 8, right: 16),
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
            decoration: BoxDecoration(color: cs.surfaceContainerHighest, borderRadius: BorderRadius.circular(Shapes.s)),
            child: Text('Esc', style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant)),
          ),
        ]),
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
    final fg = selected ? cs.onSecondaryContainer : cs.onSurface;
    final isApp = item.app != null;
    return MouseRegion(
      onHover: (_) => onHover(),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(Shapes.l),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16),
          child: Row(children: [
            SizedBox(
              width: 32,
              child: Center(
                child: isApp
                    ? AppIcon(app: item.app!, size: 28)
                    : Container(
                        width: 32,
                        height: 32,
                        decoration: BoxDecoration(color: cs.surfaceContainerHighest, shape: BoxShape.circle),
                        child: Icon(item.id == 'cmd:launchpad' ? Icons.apps_rounded : Icons.settings_rounded, size: 16, color: cs.onSurface),
                      ),
              ),
            ),
            const SizedBox(width: 16),
            Expanded(child: Text(isApp ? item.name : tr(item.name), style: TextStyle(fontSize: 16, color: fg), overflow: TextOverflow.ellipsis)),
            Text(tr(item.kind), style: TextStyle(fontSize: 12, color: selected ? cs.onSecondaryContainer : cs.onSurfaceVariant)),
          ]),
        ),
      ),
    );
  }
}
