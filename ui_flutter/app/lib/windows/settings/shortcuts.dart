library;

/// Hotkey and tiling-shortcut rules, ported from HotkeyParser.cs and TilingShortcuts.cs (the engine applies the same rules).

const _named = {
  'space', 'enter', 'return', 'esc', 'escape', 'tab', 'backspace', 'left', 'up', 'right', 'down', 'home', 'end',
  'pageup', 'pagedown', 'insert', 'delete', 'del',
};

/// Modifier bits and key of "Ctrl+Alt+Space", or null if it is not a valid global shortcut (needs a key and a modifier).
({int mods, String key})? parseHotkey(String? text) {
  if (text == null || text.trim().isEmpty) return null;
  var mods = 0;
  String? key;
  for (final raw in text.split('+').map((e) => e.trim()).where((e) => e.isNotEmpty)) {
    switch (raw.toLowerCase()) {
      case 'ctrl' || 'control':
        mods |= 2;
        continue;
      case 'alt':
        mods |= 1;
        continue;
      case 'shift':
        mods |= 4;
        continue;
      case 'win' || 'windows' || 'super':
        mods |= 8;
        continue;
    }
    if (key != null) return null;
    final lower = raw.toLowerCase();
    if (_named.contains(lower)) {
      key = lower;
    } else if (raw.length == 1 && RegExp(r'[a-zA-Z0-9]').hasMatch(raw)) {
      key = lower;
    } else if ((raw.length == 2 || raw.length == 3) && lower.startsWith('f')) {
      final n = int.tryParse(raw.substring(1));
      if (n == null || n < 1 || n > 24) return null;
      key = lower;
    } else {
      return null;
    }
  }
  return key != null && mods != 0 ? (mods: mods, key: key) : null;
}

/// null when the text is empty (shortcut off) or valid.
String? hotkeyError(String text) =>
    text.trim().isEmpty || parseHotkey(text) != null ? null : 'Not a valid shortcut. Example: Ctrl+Alt+Space';

class TilingShortcut {
  const TilingShortcut(this.id, this.label, this.defaultValue, [this.arg]);
  final String id;

  /// English label; `{0}` is replaced by [arg] after translation.
  final String label;
  final String defaultValue;
  final int? arg;
}

List<TilingShortcut> tilingShortcuts({required bool altModifier, int workspaces = 9}) {
  final mod = altModifier ? 'Alt' : 'Ctrl+Alt+Win';
  final items = <TilingShortcut>[];
  for (final (key, dir) in const [('H', 'left'), ('J', 'down'), ('K', 'up'), ('L', 'right')]) {
    final cap = '${dir[0].toUpperCase()}${dir.substring(1)}';
    items.add(TilingShortcut('focus.$dir', 'Focus $dir', '$mod+$key'));
    items.add(TilingShortcut('move.$dir', 'Move $dir', '$mod+Shift+$key'));
    items.add(TilingShortcut('resize.$dir', 'Resize $dir', 'Ctrl+Alt+Shift+$key'));
    assert(cap.isNotEmpty);
  }
  for (var i = 1; i <= workspaces; i++) {
    items.add(TilingShortcut('workspace.$i', 'Workspace {0}', '$mod+$i', i));
    items.add(TilingShortcut('send.$i', 'Send to workspace {0}', '$mod+Shift+$i', i));
  }
  items.add(TilingShortcut('floating', 'Toggle floating', '$mod+F'));
  items.add(TilingShortcut('split', 'Toggle split direction', '$mod+V'));
  items.add(TilingShortcut('pause', 'Pause / resume tiling', '$mod+P'));
  items.add(const TilingShortcut('workspace.previous', 'Previous workspace', ''));
  items.add(const TilingShortcut('workspace.next', 'Next workspace', ''));
  return items;
}

/// First problem of the whole set of shortcuts (invalid text, or one combination used twice), or null.
String? tilingShortcutsError({
  required bool altModifier,
  required int workspaces,
  required Map<String, String> overrides,
  required List<String> otherHotkeys,
}) {
  final used = <String>{};
  String id(({int mods, String key}) h) => '${h.mods}:${h.key}';
  for (final text in otherHotkeys) {
    final h = parseHotkey(text);
    if (h != null) used.add(id(h));
  }
  for (final s in tilingShortcuts(altModifier: altModifier, workspaces: workspaces)) {
    final text = overrides[s.id] ?? s.defaultValue;
    if (text.trim().isEmpty) continue;
    final h = parseHotkey(text);
    if (h == null) return 'Not a valid shortcut. Example: Ctrl+Alt+Space';
    if (!used.add(id(h))) return 'This shortcut is already assigned to another action.';
  }
  return null;
}
