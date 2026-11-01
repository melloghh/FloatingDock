import 'package:flutter/material.dart';

import '../../core/app_state.dart';
import '../../ui/glide_scope.dart';
import 'popup_view.dart';

/// One entry of a context menu (dock icon, Launchpad tile...).
class ContextItem {
  const ContextItem(this.label, {this.icon, this.onTap, this.checked = false, this.enabled = true, this.translate = true}) : separator = false;
  const ContextItem.separator()
      : label = '',
        icon = null,
        onTap = null,
        checked = false,
        enabled = false,
        translate = false,
        separator = true;

  final String label;
  final IconData? icon;
  final VoidCallback? onTap;
  final bool checked, enabled, separator, translate;
}

const contextMenuWidth = 268.0;

/// Height of a context menu's panel, so a menu that opens upwards can be placed before it is drawn.
double contextMenuHeight(List<ContextItem> items) {
  var h = 16.0; // panel padding
  for (final i in items) {
    h += i.separator ? 13 : 48;
  }
  return h;
}

/// The Material 3 context menu: a list of rows with one highlight gliding between them.
class ContextMenuContent extends StatelessWidget {
  const ContextMenuContent({super.key, required this.items});
  final List<ContextItem> items;

  @override
  Widget build(BuildContext context) {
    final popup = PopupScope.of(context);
    return GlideScope(
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        for (final item in items)
          if (item.separator)
            const MenuDivider()
          else
            MenuRow(
              title: item.label,
              icon: item.icon,
              selected: item.checked,
              enabled: item.enabled,
              translate: item.translate,
              onTap: () {
                popup.close();
                item.onTap?.call();
              },
            ),
      ]),
    );
  }
}

/// Used by tests and previews: a context menu inside the popup surface.
Widget contextMenuFor(List<ContextItem> items) => ContextMenuContent(items: items);

String contextLabel(ContextItem i) => i.translate ? tr(i.label) : i.label;
