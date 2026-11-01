import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../core/app_state.dart';
import '../core/theme.dart';

/// A line of explanatory text between rows (no card).
class NoteText extends StatelessWidget {
  const NoteText(this.text, {super.key});
  final String text;

  @override
  Widget build(BuildContext context) {
    if (text.isEmpty) return const SizedBox.shrink();
    final cs = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 6, 16, 6),
      child: Text(tr(text), style: TextStyle(color: cs.onSurfaceVariant, fontSize: 12.5, height: 1.4)),
    );
  }
}

/// Rows after this marker are only shown when the user expands "Advanced".
class Advanced extends StatelessWidget {
  const Advanced(this.children, {super.key});
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => const SizedBox.shrink();
}

/// A labelled group of rows: a primary-coloured label, then rows that share one shape: 24 dp outer corners,
/// 6 dp between neighbours and 3 dp gaps.
class Group extends StatefulWidget {
  const Group({super.key, this.title, required this.children});
  final String? title;
  final List<Widget> children;

  @override
  State<Group> createState() => _GroupState();
}

class _GroupState extends State<Group> {
  bool _advancedOpen = false;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final items = <Widget>[];
    for (final c in widget.children) {
      if (c is Advanced) {
        items.add(_AdvancedToggle(open: _advancedOpen, onTap: () => setState(() => _advancedOpen = !_advancedOpen)));
        if (_advancedOpen) items.addAll(c.children);
      } else {
        items.add(c);
      }
    }
    final out = <Widget>[];
    var run = <Widget>[];
    void flush() {
      for (var i = 0; i < run.length; i++) {
        final top = i == 0 ? 24.0 : 6.0, bottom = i == run.length - 1 ? 24.0 : 6.0;
        out.add(Padding(
          padding: EdgeInsets.only(top: i == 0 ? 0 : 3),
          child: Material(
            color: cs.surfaceContainerHigh,
            clipBehavior: Clip.antiAlias,
            shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.vertical(top: Radius.circular(top), bottom: Radius.circular(bottom))),
            child: ConstrainedBox(constraints: const BoxConstraints(minHeight: 56), child: run[i]),
          ),
        ));
      }
      run = [];
    }

    for (final item in items) {
      if (item is NoteText || item is ButtonRow) {
        flush();
        out.add(item);
      } else {
        run.add(item);
      }
    }
    flush();
    return Padding(
      padding: const EdgeInsets.only(top: 14),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        if (widget.title != null)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
            child: Text(tr(widget.title!),
                style: TextStyle(color: cs.primary, fontSize: 13.5, fontWeight: FontWeight.w600, letterSpacing: .2)),
          ),
        ...out,
      ]),
    );
  }
}

class _AdvancedToggle extends StatelessWidget {
  const _AdvancedToggle({required this.open, required this.onTap});
  final bool open;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return InkWell(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        child: Row(children: [
          Expanded(child: Text(tr('Advanced'), style: const TextStyle(fontSize: 14.5, fontWeight: FontWeight.w500))),
          AnimatedRotation(
            turns: open ? .5 : 0,
            duration: const Duration(milliseconds: 220),
            curve: emphasized,
            child: Icon(Icons.expand_more_rounded, color: cs.onSurfaceVariant),
          ),
        ]),
      ),
    );
  }
}

class _RowPadding extends StatelessWidget {
  const _RowPadding({required this.child, this.onTap});
  final Widget child;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final padded = Padding(padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8), child: child);
    return onTap == null ? padded : InkWell(onTap: onTap, child: padded);
  }
}

TextStyle rowLabel(BuildContext context) =>
    TextStyle(fontSize: 14.5, fontWeight: FontWeight.w500, color: Theme.of(context).colorScheme.onSurface);

class ToggleRow extends StatelessWidget {
  const ToggleRow({super.key, required this.label, required this.value, required this.onChanged});
  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => _RowPadding(
        onTap: () => onChanged(!value),
        child: Row(children: [
          Expanded(child: Text(tr(label), style: rowLabel(context))),
          const SizedBox(width: 12),
          Switch(value: value, onChanged: onChanged),
        ]),
      );
}

class SliderRow extends StatelessWidget {
  const SliderRow({
    super.key,
    required this.label,
    required this.min,
    required this.max,
    required this.value,
    required this.onChanged,
    this.suffix = '',
  });
  final String label;
  final int min, max, value;
  final String suffix;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return _RowPadding(
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          Expanded(child: Text(tr(label), style: rowLabel(context))),
          Text('$value${tr(suffix)}', style: TextStyle(color: cs.primary, fontWeight: FontWeight.w600, fontSize: 13.5)),
        ]),
        SliderTheme(
          data: SliderTheme.of(context).copyWith(trackHeight: 16),
          child: Slider(
            // ignore: deprecated_member_use
            year2023: false,
            value: value.clamp(min, max).toDouble(),
            min: min.toDouble(),
            max: max.toDouble(),
            divisions: max - min <= 12 ? max - min : null,
            onChanged: (v) => onChanged(v.round()),
          ),
        ),
      ]),
    );
  }
}

class ChoiceRow<T> extends StatelessWidget {
  const ChoiceRow({super.key, required this.label, required this.items, required this.value, required this.onChanged});
  final String label;
  final List<(String, T)> items;
  final T value;
  final ValueChanged<T> onChanged;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final current = items.firstWhere((e) => e.$2 == value, orElse: () => items.first);
    return _RowPadding(
      child: Row(children: [
        Expanded(child: Text(tr(label), style: rowLabel(context))),
        const SizedBox(width: 12),
        MenuAnchor(
          style: MenuStyle(
            backgroundColor: WidgetStatePropertyAll(cs.surfaceContainerHigh),
            surfaceTintColor: const WidgetStatePropertyAll(Colors.transparent),
            shape: WidgetStatePropertyAll(RoundedRectangleBorder(borderRadius: BorderRadius.circular(Shapes.l))),
            padding: const WidgetStatePropertyAll(EdgeInsets.symmetric(vertical: 8)),
          ),
          menuChildren: [
            for (final (text, v) in items)
              MenuItemButton(
                onPressed: () => onChanged(v),
                style: ButtonStyle(
                  backgroundColor: WidgetStatePropertyAll(v == value ? cs.secondaryContainer : Colors.transparent),
                  shape: WidgetStatePropertyAll(RoundedRectangleBorder(borderRadius: BorderRadius.circular(Shapes.m))),
                  minimumSize: const WidgetStatePropertyAll(Size(220, 44)),
                ),
                child: Text(tr(text)),
              ),
          ],
          builder: (context, controller, _) => FilledButton.tonal(
            onPressed: () => controller.isOpen ? controller.close() : controller.open(),
            style: FilledButton.styleFrom(
              minimumSize: const Size(0, 40),
              padding: const EdgeInsets.only(left: 16, right: 10),
              backgroundColor: cs.surfaceContainerHighest,
              foregroundColor: cs.onSurface,
            ),
            child: Row(mainAxisSize: MainAxisSize.min, children: [
              Flexible(child: Text(tr(current.$1), overflow: TextOverflow.ellipsis)),
              const SizedBox(width: 6),
              const Icon(Icons.arrow_drop_down_rounded),
            ]),
          ),
        ),
      ]),
    );
  }
}

/// A labelled text field that applies its value when it is valid. [validate] returns an error text or null.
class TextRow extends StatefulWidget {
  const TextRow({super.key, required this.label, required this.value, required this.onChanged, this.validate, this.hint, this.trailing});
  final String label;
  final String value;
  final String? hint;
  final String? Function(String text)? validate;
  final ValueChanged<String> onChanged;
  final Widget? trailing;

  @override
  State<TextRow> createState() => _TextRowState();
}

class _TextRowState extends State<TextRow> {
  late final TextEditingController _controller = TextEditingController(text: widget.value);
  final FocusNode _focus = FocusNode();
  String? _error;

  @override
  void didUpdateWidget(TextRow old) {
    super.didUpdateWidget(old);
    // Follow outside changes (another window, a reset) unless the user is typing here.
    if (!_focus.hasFocus && widget.value != _controller.text) _controller.text = widget.value;
  }

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  void _apply(String text) {
    final error = widget.validate?.call(text);
    setState(() => _error = error == null ? null : tr(error));
    if (error == null) widget.onChanged(text);
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return _RowPadding(
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Padding(padding: const EdgeInsets.only(bottom: 8, top: 4), child: Text(tr(widget.label), style: rowLabel(context))),
        Row(children: [
          Expanded(
            child: TextField(
              controller: _controller,
              focusNode: _focus,
              onChanged: _apply,
              onSubmitted: _apply,
              style: const TextStyle(fontSize: 14),
              decoration: InputDecoration(
                hintText: widget.hint,
                isDense: true,
                errorText: _error,
                errorStyle: TextStyle(color: cs.error),
                enabledBorder: _error == null
                    ? null
                    : OutlineInputBorder(borderRadius: BorderRadius.circular(Shapes.m), borderSide: BorderSide(color: cs.error, width: 2)),
              ),
            ),
          ),
          if (widget.trailing != null) ...[const SizedBox(width: 8), widget.trailing!],
        ]),
        const SizedBox(height: 4),
      ]),
    );
  }
}

/// A row with just a title and a trailing widget (buttons, icons).
class ListRow extends StatelessWidget {
  const ListRow({super.key, required this.title, this.leading, this.subtitle, this.trailing = const []});
  final String title;
  final String? subtitle;
  final Widget? leading;
  final List<Widget> trailing;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return _RowPadding(
      child: Row(children: [
        if (leading != null) ...[leading!, const SizedBox(width: 12)],
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(title, style: rowLabel(context), overflow: TextOverflow.ellipsis),
            if (subtitle != null) Text(subtitle!, style: TextStyle(color: cs.onSurfaceVariant, fontSize: 12), overflow: TextOverflow.ellipsis),
          ]),
        ),
        ...trailing,
      ]),
    );
  }
}

class RowIconButton extends StatelessWidget {
  const RowIconButton({super.key, required this.icon, required this.tooltip, required this.onPressed});
  final IconData icon;
  final String tooltip;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) => IconButton(
        icon: Icon(icon, size: 20),
        tooltip: tooltip.isEmpty ? null : tr(tooltip),
        onPressed: onPressed,
        color: Theme.of(context).colorScheme.onSurfaceVariant,
      );
}

/// Pill buttons laid out in a wrapping row (not a card).
class ButtonRow extends StatelessWidget {
  const ButtonRow(this.children, {super.key});
  final List<Widget> children;

  @override
  Widget build(BuildContext context) =>
      Padding(padding: const EdgeInsets.fromLTRB(0, 8, 0, 4), child: Wrap(spacing: 8, runSpacing: 8, children: children));
}

class PillButton extends StatelessWidget {
  const PillButton({super.key, required this.label, required this.onPressed, this.icon, this.filled = false, this.danger = false});
  final String label;
  final VoidCallback? onPressed;
  final IconData? icon;
  final bool filled;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final child = Row(mainAxisSize: MainAxisSize.min, children: [
      if (icon != null) ...[Icon(icon, size: 18), const SizedBox(width: 8)],
      Text(tr(label)),
    ]);
    final style = ButtonStyle(
      minimumSize: const WidgetStatePropertyAll(Size(0, 40)),
      padding: WidgetStatePropertyAll(EdgeInsets.symmetric(horizontal: icon == null ? 20 : 16)),
      foregroundColor: danger ? WidgetStatePropertyAll(cs.error) : null,
    );
    return filled
        ? FilledButton(onPressed: onPressed, style: style, child: child)
        : FilledButton.tonal(onPressed: onPressed, style: style, child: child);
  }
}

/// Round swatches of the colour the whole palette is generated from; graphite is second after mint, as in the reference.
class SeedPickerRow extends StatelessWidget {
  const SeedPickerRow({super.key, required this.label, required this.value, required this.onChanged});
  final String label;
  final String value;
  final ValueChanged<String> onChanged;

  static const seeds = [
    ('Mint', '4E9F75'), ('Graphite', '5F6368'), ('Blue', '4A7BD0'), ('Teal', '2E8B7F'), ('Gold', 'C9A227'),
    ('Orange', 'D9732B'), ('Red', 'C8484A'), ('Violet', '8A5CC8'), ('Pink', 'C2578C'),
  ];

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return _RowPadding(
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Padding(padding: const EdgeInsets.only(top: 4, bottom: 12), child: Text(tr(label), style: rowLabel(context))),
        Wrap(spacing: 10, runSpacing: 10, children: [
          for (final (name, hex) in seeds)
            Tooltip(
              message: tr(name),
              child: InkResponse(
                onTap: () => onChanged(hex),
                radius: 26,
                child: AnimatedContainer(
                  duration: const Duration(milliseconds: 200),
                  curve: emphasized,
                  width: 44,
                  height: 44,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: Color(0xFF000000 | int.parse(hex, radix: 16)),
                    border: Border.all(color: hex.toUpperCase() == value.toUpperCase() ? cs.onSurface : Colors.transparent, width: 3),
                  ),
                  child: hex.toUpperCase() == value.toUpperCase()
                      ? Icon(Icons.check_rounded, size: 20, color: ThemeData.estimateBrightnessForColor(Color(0xFF000000 | int.parse(hex, radix: 16))) == Brightness.dark ? Colors.white : Colors.black)
                      : null,
                ),
              ),
            ),
        ]),
        const SizedBox(height: 4),
      ]),
    );
  }
}

/// A modal M3 dialog inside the window (used for confirmations and messages).
Future<bool> confirmDialog(BuildContext context, String title, String body, {String ok = 'OK', String cancel = 'Cancel', bool danger = false}) async {
  final result = await showDialog<bool>(
    context: context,
    builder: (context) {
      final cs = Theme.of(context).colorScheme;
      return AlertDialog(
        title: Text(tr(title)),
        content: ConstrainedBox(constraints: const BoxConstraints(maxWidth: 420), child: Text(tr(body))),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: Text(tr(cancel))),
          TextButton(
            onPressed: () => Navigator.pop(context, true),
            style: danger ? TextButton.styleFrom(foregroundColor: cs.error) : null,
            child: Text(tr(ok)),
          ),
        ],
      );
    },
  );
  return result ?? false;
}

Future<void> messageDialog(BuildContext context, String title, String body) async {
  await showDialog<void>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(tr(title)),
      content: ConstrainedBox(constraints: const BoxConstraints(maxWidth: 420), child: Text(body)),
      actions: [TextButton(onPressed: () => Navigator.pop(context), child: Text(tr('OK')))],
    ),
  );
}

/// Convenience for rows that bind straight to a setting.
extension SettingRows on AppState {
  Widget toggle(String key, String label) => ToggleRow(label: label, value: setting<bool>(key, false), onChanged: (v) => update({key: v}));

  Widget slider(String key, String label, int min, int max, {String suffix = ''}) =>
      SliderRow(label: label, min: min, max: max, value: setting<int>(key, min), suffix: suffix, onChanged: (v) => update({key: v}));

  Widget choice(String key, String label, List<(String, String)> items) =>
      ChoiceRow<String>(label: label, items: items, value: setting<String>(key, items.first.$2), onChanged: (v) => update({key: v}));

  Widget text(String key, String label, {String? Function(String)? validate, String? hint, Widget? trailing, String Function(String)? normalize}) =>
      TextRow(
        label: label,
        value: setting<String>(key, ''),
        hint: hint,
        validate: validate,
        trailing: trailing,
        onChanged: (v) => update({key: normalize == null ? v : normalize(v)}),
      );
}

/// 6 hex digits, with or without '#'.
String? hexColorError(String text) =>
    RegExp(r'^#?[0-9a-fA-F]{6}$').hasMatch(text.trim()) ? null : 'Use 6 hex digits, e.g. 1E1E1E';

String normalizeHex(String text) => text.trim().replaceFirst('#', '').toUpperCase();

/// Copies text to the clipboard (used by "copy path" style buttons).
Future<void> copyText(String text) => Clipboard.setData(ClipboardData(text: text));
