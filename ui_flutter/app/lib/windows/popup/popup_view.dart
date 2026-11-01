import 'package:flutter/material.dart';

import '../../core/app_state.dart';
import '../../core/theme.dart';
import '../../ui/glide_scope.dart';
import '../../ui/motion.dart';

/// State of the one reusable popup window: which menu it shows and how it is anchored.
class PopupModel extends ChangeNotifier {
  /// Pop state of the surface; false while the window is hidden.
  final visible = ValueNotifier<bool>(false);

  WidgetBuilder? builder;
  String kind = '';
  double panelWidth = 334;
  double maxHeight = 600;

  /// Where along the panel's top edge the surface grows from, 0 (left) .. 1 (right): the anchor it springs out of.
  double pivot = .5;

  /// Increases on every open: the content is rebuilt from scratch and animates in.
  int revision = 0;

  void Function()? onClose;

  void open({required String kind, required WidgetBuilder builder, required double panelWidth, required double maxHeight, required double pivot}) {
    this.kind = kind;
    this.builder = builder;
    this.panelWidth = panelWidth;
    this.maxHeight = maxHeight;
    this.pivot = pivot.clamp(0.0, 1.0);
    revision++;
    notifyListeners();
  }

  void close() => onClose?.call();
}

/// Lets menu content close the popup or read what it was opened for.
class PopupScope extends InheritedWidget {
  const PopupScope({super.key, required this.model, required super.child});
  final PopupModel model;

  static PopupModel of(BuildContext context) => context.getInheritedWidgetOfExactType<PopupScope>()!.model;

  @override
  bool updateShouldNotify(PopupScope old) => false;
}

/// A popup surface: a rounded container that pops out of its anchor, holding the content of a menu.
class PopupView extends StatelessWidget {
  const PopupView({super.key, required this.model});
  final PopupModel model;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return PopupScope(
      model: model,
      child: ListenableBuilder(
        listenable: model,
        builder: (context, _) => GestureDetector(
          behavior: HitTestBehavior.translucent,
          onTap: model.close, // the transparent margin counts as "outside"
          child: Padding(
            padding: const EdgeInsets.all(28),
            child: Align(
              alignment: Alignment.topLeft,
              child: ValueListenableBuilder<bool>(
                valueListenable: model.visible,
                builder: (context, visible, child) => PopSurface(visible: visible, origin: Alignment(model.pivot * 2 - 1, -1), child: child!),
                child: GestureDetector(
                  onTap: () {},
                  child: Container(
                    width: model.panelWidth,
                    constraints: BoxConstraints(maxHeight: model.maxHeight),
                    decoration: BoxDecoration(
                      color: cs.surfaceContainerHigh,
                      borderRadius: BorderRadius.circular(Shapes.xl),
                      border: Border.all(color: cs.outlineVariant.withValues(alpha: .35)),
                      boxShadow: const [BoxShadow(color: Color(0x2E000000), blurRadius: 28, offset: Offset(0, 6))],
                    ),
                    clipBehavior: Clip.antiAlias,
                    child: Material(
                      type: MaterialType.transparency,
                      child: SingleChildScrollView(
                        padding: const EdgeInsets.all(16),
                        child: model.builder == null
                            ? const SizedBox.shrink()
                            : Reveal(
                                key: ValueKey(model.revision),
                                distance: 10,
                                blur: 6,
                                milliseconds: 450,
                                child: Builder(builder: model.builder!),
                              ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------- shared menu pieces

/// A Material list item: leading icon, one or two lines of text, a trailing check when it is the current choice.
class MenuRow extends StatelessWidget {
  const MenuRow({super.key, required this.title, this.detail = '', this.icon, this.selected = false, this.enabled = true, required this.onTap, this.translate = true});
  final String title, detail;
  final IconData? icon;
  final bool selected, enabled, translate;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final fg = selected ? cs.onSecondaryContainer : cs.onSurface;
    return GlideRow(
      onTap: onTap,
      enabled: enabled,
      child: Container(
        constraints: BoxConstraints(minHeight: detail.isEmpty ? 48 : 56),
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
        decoration: BoxDecoration(color: selected ? cs.secondaryContainer : Colors.transparent, borderRadius: BorderRadius.circular(Shapes.l)),
        child: Row(children: [
          if (icon != null) ...[Icon(icon, size: 20, color: selected ? cs.onSecondaryContainer : cs.onSurfaceVariant), const SizedBox(width: 16)],
          Expanded(
            child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(translate ? tr(title) : title, style: TextStyle(fontSize: 14, color: enabled ? fg : fg.withValues(alpha: .4)), overflow: TextOverflow.ellipsis),
              if (detail.isNotEmpty) Text(detail, style: TextStyle(fontSize: 12, color: selected ? cs.onSecondaryContainer : cs.onSurfaceVariant), overflow: TextOverflow.ellipsis),
            ]),
          ),
          if (selected) Icon(Icons.check_rounded, size: 16, color: cs.onSecondaryContainer),
        ]),
      ),
    );
  }
}

class MenuHeader extends StatelessWidget {
  const MenuHeader(this.title, this.icon, {super.key});
  final String title;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.fromLTRB(2, 0, 2, 12),
      child: Row(children: [
        Icon(icon, size: 22, color: cs.primary),
        const SizedBox(width: 12),
        Text(tr(title), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w500)),
      ]),
    );
  }
}

class MenuDivider extends StatelessWidget {
  const MenuDivider({super.key});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 6),
        child: Divider(height: 1, color: Theme.of(context).colorScheme.outlineVariant.withValues(alpha: .6)),
      );
}

class MenuNote extends StatelessWidget {
  const MenuNote(this.text, {super.key, this.translate = true});
  final String text;
  final bool translate;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 4),
        child: Text(translate ? tr(text) : text, style: TextStyle(fontSize: 12, color: Theme.of(context).colorScheme.onSurfaceVariant, height: 1.35)),
      );
}
