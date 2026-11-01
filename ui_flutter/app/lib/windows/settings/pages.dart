import 'package:flutter/material.dart';

import '../../core/app_state.dart';
import '../../core/native.dart';
import '../../ui/app_icon.dart';
import '../../ui/live_data.dart';
import '../../ui/m3.dart';
import 'shortcuts.dart';

/// Settings pages: name, icon and the rows each shows. English names are the keys of the translation table.
class SettingsPageDef {
  const SettingsPageDef(this.name, this.icon, this.build);
  final String name;
  final IconData icon;
  final List<Widget> Function(BuildContext context, AppState s) build;
}

final settingsPages = <SettingsPageDef>[
  SettingsPageDef('Dock', Icons.dock_rounded, dockPage),
  SettingsPageDef('Appearance', Icons.palette_rounded, appearancePage),
  SettingsPageDef('Magnification', Icons.zoom_in_rounded, magnificationPage),
  SettingsPageDef('Behaviour', Icons.tune_rounded, behaviourPage),
  SettingsPageDef('Menu Bar', Icons.web_asset_rounded, menuBarPage),
  SettingsPageDef('Icon Packs', Icons.interests_rounded, iconPacksPage),
  SettingsPageDef('Launchpad', Icons.apps_rounded, launchpadPage),
  SettingsPageDef('Search', Icons.search_rounded, searchPage),
  SettingsPageDef('Tiling manager', Icons.grid_view_rounded, tilingPage),
  SettingsPageDef('Windows Taskbar', Icons.call_to_action_rounded, taskbarPage),
  SettingsPageDef('About', Icons.info_outline_rounded, aboutPage),
];

// ---------------------------------------------------------------------- Menu bar

List<Widget> menuBarPage(BuildContext context, AppState s) => [
      Group(children: [
        s.toggle('TopBarEnabled', 'Show the top menu bar'),
        s.toggle('TopBarAutoHide', 'Auto-hide — reveal at the top edge'),
        s.toggle('TopBarReserveSpace', 'Keep maximized windows below the menu bar'),
        s.toggle('TopBarShowDate', 'Show date next to the clock'),
        s.toggle('TopBarMediaEnabled', 'Show media controls'),
        s.toggle('TopBarTrayEnabled', 'Show system tray arrow'),
        s.toggle('TopBarSearchEnabled', 'Show search button'),
      ]),
      Group(title: 'Motion', children: [
        s.slider('TopBarAnimationFps', 'Hide animation frame limit', 30, 144, suffix: ' fps'),
        s.slider('TopBarShowMs', 'Show duration', 80, 800, suffix: ' ms'),
        s.slider('TopBarHideMs', 'Hide duration', 80, 800, suffix: ' ms'),
        s.slider('TopBarHideDelayMs', 'Hide delay', 100, 3000, suffix: ' ms'),
      ]),
      Group(children: [s.toggle('TopBarShowSeconds', 'Show seconds')]),
      const NoteText(
          'Current app on the left; network, sound, input language and clock on the right. Hidden during fullscreen and screen capture. Follows the Dock monitor setting.'),
    ];

// ---------------------------------------------------------------------- Dock

List<Widget> dockPage(BuildContext context, AppState s) => [
      Group(title: 'Size & spacing', children: [
        s.slider('IconSize', 'Icon size', 24, 128, suffix: ' px'),
        s.slider('IconSpacing', 'Icon spacing', 0, 40, suffix: ' px'),
        s.slider('DockHeight', 'Dock height', 28, 200, suffix: ' px'),
      ]),
      Group(title: 'Position', children: [
        s.choice('Edge', 'Dock position', const [('Bottom', 'Bottom'), ('Left', 'Left'), ('Right', 'Right')]),
        s.slider('BottomOffset', 'Distance from the screen edge', 0, 200, suffix: ' px'),
        Advanced([
          s.choice('WidthMode', 'Width', const [('Fit the icons', 'Auto'), ('Fixed width', 'Fixed')]),
          s.slider('FixedWidth', 'Fixed width', 200, 3000, suffix: ' px'),
          s.choice('Alignment', 'Alignment', const [('Center', 'Center'), ('Left', 'Left'), ('Right', 'Right')]),
          s.slider('HorizontalOffset', 'Offset along the edge', -1500, 1500, suffix: ' px'),
          s.choice('MonitorMode', 'Monitors', const [('Primary monitor only', 'Primary'), ('Every monitor', 'All')]),
        ]),
      ]),
      const PinnedSection(),
    ];

class PinnedSection extends StatelessWidget {
  const PinnedSection({super.key});

  @override
  Widget build(BuildContext context) {
    final s = AppScope.of(context);
    final apps = s.pinned.where((a) => a['IsSeparator'] != true).toList();
    return Group(title: 'Pinned apps', children: [
      if (apps.isEmpty) const NoteText('Nothing is pinned yet.'),
      for (var i = 0; i < apps.length; i++)
        ListRow(
          title: '${apps[i]['Name']}',
          leading: AppIcon(app: apps[i], size: 28),
          trailing: [
            RowIconButton(
                icon: Icons.arrow_back_rounded,
                tooltip: 'Move left',
                onPressed: i > 0 ? () => s.call('pinned.move', {'key': apps[i]['Key'], 'index': i - 1}) : null),
            RowIconButton(
                icon: Icons.arrow_forward_rounded,
                tooltip: 'Move right',
                onPressed: i < apps.length - 1 ? () => s.call('pinned.move', {'key': apps[i]['Key'], 'index': i + 1}) : null),
            RowIconButton(icon: Icons.push_pin_outlined, tooltip: 'Unpin', onPressed: () => s.call('pinned.remove', {'key': apps[i]['Key']})),
          ],
        ),
      ButtonRow([
        PillButton(
          label: 'Add app…',
          icon: Icons.add_rounded,
          onPressed: () async {
            final path = await native.pickFile(View.of(context).viewId, filters: const [('Applications and shortcuts', '*.exe;*.lnk')]);
            if (path == null || !context.mounted) return;
            final r = await s.call('pinned.add', {'path': path}) as Map?;
            if (r != null && r['status'] != 'Added' && context.mounted) {
              await messageDialog(context, 'Pinned apps', '${r['message'] ?? tr('This file type is not supported.')}');
            }
          },
        ),
        PillButton(
          label: 'Import pins from the Windows taskbar',
          icon: Icons.download_rounded,
          onPressed: () async {
            final r = await s.call('pinned.importTaskbar') as Map?;
            final n = (r?['added'] as num?)?.toInt() ?? 0;
            if (context.mounted) {
              await messageDialog(context, 'Pinned apps', n > 0 ? trf('Imported {0} app(s).', [n]) : tr('No new pinned apps were found on the Windows taskbar.'));
            }
          },
        ),
      ]),
      const NoteText('Drag icons in the Dock to reorder them, drag one out of the Dock to unpin it.'),
    ]);
  }
}

// ---------------------------------------------------------------------- Appearance

List<Widget> appearancePage(BuildContext context, AppState s) => [
      Group(children: [
        s.choice('Language', 'Interface language', const [('English', 'English'), ('Russian', 'Russian')]),
      ]),
      Group(title: 'Background', children: [
        s.text('BackgroundColor', 'Background color (hex RGB)', validate: hexColorError, normalize: normalizeHex),
        s.slider('Opacity', 'Opacity', 0, 100, suffix: ' %'),
        s.slider('CornerRadius', 'Corner radius', 0, 60, suffix: ' px'),
        s.toggle('Shadow', 'Drop shadow'),
        s.toggle('Border', 'Thin border'),
        s.toggle('MenuAnimations', 'Animate menus'),
        s.slider('MenuAnimationMs', 'Menu animation duration', 80, 600, suffix: ' ms'),
      ]),
      Group(title: 'Theme & indicators', children: [
        s.choice('Theme', 'Theme (settings, Launchpad menus)', const [('Dark', 'Dark'), ('Light', 'Light'), ('Follow system', 'System')]),
        SeedPickerRow(label: 'Accent color', value: s.seed, onChanged: (v) => s.update({'AccentSeed': v})),
        s.text('AccentSeed', 'Accent color (hex RGB)', hint: '4E9F75', validate: (t) => hexColorError(t)?.replaceFirst('1E1E1E', '4E9F75'), normalize: normalizeHex),
        s.choice('IndicatorStyle', 'Running indicator', const [('Dot', 'Dot'), ('Bar', 'Bar')]),
        s.toggle('ShowLabels', 'Show app names above icons'),
      ]),
    ];

// ---------------------------------------------------------------------- Magnification

List<Widget> magnificationPage(BuildContext context, AppState s) => [
      Group(children: [
        s.toggle('MagnificationEnabled', 'Magnify icons under the cursor'),
        s.slider('MaxScalePercent', 'Maximum size', 100, 300, suffix: ' %'),
        s.slider('MagnificationRadius', 'Reach around the cursor', 40, 500, suffix: ' px'),
      ]),
      Group(title: 'Motion', children: [
        s.slider('AnimationFps', 'Animation frame limit', 30, 144, suffix: ' fps'),
        const NoteText('60 fps reduces CPU usage. Higher values follow high-refresh displays more closely.'),
        s.slider('Responsiveness', 'Responsiveness (follow the cursor)', 1, 10),
        s.slider('AnimationSpeed', 'Animation speed (zoom in / out, auto-hide)', 1, 10),
        s.slider('Smoothness', 'Smoothness (easing)', 0, 10),
        s.toggle('IconAnimation', 'Animate icons appearing / disappearing / reordering'),
        s.slider('IconAnimationMs', 'Appear / disappear duration', 80, 800, suffix: ' ms'),
      ]),
    ];

// ---------------------------------------------------------------------- Behaviour

/// A shortcut field that also says when Windows refused the registration (another program owns it).
Widget hotkeyField(AppState s, String key, String label, String statusKey, String hint) => LiveData<Map?>(
      refreshOn: const {'hotkeys.changed'},
      load: (state) async => await state.call('hotkey.status') as Map?,
      builder: (context, status, state) {
        final text = state.setting<String>(key, '');
        final refused = status != null && status[statusKey] == false && text.trim().isNotEmpty;
        return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          state.text(key, label, validate: hotkeyError, hint: hint),
          if (refused)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
              child: Text(tr('This shortcut is already used by another program.'), style: TextStyle(color: Theme.of(context).colorScheme.error, fontSize: 12.5)),
            ),
        ]);
      },
    );

List<Widget> behaviourPage(BuildContext context, AppState s) => [
      Group(title: 'What the dock shows', children: [
        s.toggle('ShowStartButton', 'Start button'),
        s.toggle('ShowLaunchpadButton', 'Launchpad button'),
        s.toggle('ShowPinned', 'Pinned apps'),
        s.toggle('ShowRunning', 'Running apps that are not pinned'),
        s.toggle('ShowActiveIndicator', 'Highlight the active app'),
      ]),
      Group(title: 'Hiding', children: [
        s.toggle('AutoHide', 'Auto-hide the dock'),
        s.toggle('RevealOnEdgeHover', 'Reveal when the cursor touches the screen edge'),
        s.toggle('HideOnFullscreen', 'Hide for fullscreen or maximized apps'),
        s.toggle('KeepDockInFullscreen', 'Keep the dock visible: fullscreen windows open above it'),
        const NoteText(
            "The dock's strip stays free and windows end above it. Needs the Windows taskbar auto-hidden or hidden and the dock not set to auto-hide. Games in exclusive full screen still hide the dock."),
      ]),
      Group(title: 'Window previews', children: [
        s.toggle('ShowWindowPreviews', 'Show window previews on hover'),
        s.slider('PreviewDelayMs', 'Preview delay', 200, 2000, suffix: ' ms'),
      ]),
      Group(title: 'Clicks & startup', children: [
        s.toggle('MinimizeOnRepeatClick', 'Clicking the active app minimizes it'),
        s.toggle('StartWithWindows', 'Start FloatingDock with Windows'),
        hotkeyField(s, 'DockFocusHotkey', 'Keyboard focus hotkey (arrows, Enter, Esc inside the dock)', 'dockFocus', 'Ctrl+Alt+D'),
      ]),
    ];

// ---------------------------------------------------------------------- Launchpad

List<Widget> launchpadPage(BuildContext context, AppState s) => [
      Group(children: [
        s.choice('LaunchpadIconStyle', 'Launchpad icon', const [
          ('White squares', 'WhiteSquares'),
          ('Color on dark', 'ColorOnDark'),
          ('Color on white', 'ColorOnWhite'),
        ]),
      ]),
      Group(title: 'Tiles', children: [
        s.slider('LaunchpadIconSize', 'Icon size', 32, 160, suffix: ' px'),
        s.slider('LaunchpadSpacing', 'Spacing', 4, 120, suffix: ' px'),
      ]),
      Group(title: 'Shortcut', children: [hotkeyField(s, 'LaunchpadHotkey', 'Open / close Launchpad', 'launchpad', 'Ctrl+Alt+Space')]),
      LiveData<List?>(
        refreshOn: const {'tiles.changed'},
        load: (state) async => await state.call('tiles.get') as List?,
        builder: (context, tiles, state) {
          final list = (tiles ?? const []).cast<Map>().map((e) => e.cast<String, Object?>()).toList();
          return Group(title: 'Tiles you added', children: [
            if (list.isEmpty) const NoteText('No tiles added yet. Drag a .lnk or .exe file onto the open Launchpad.'),
            for (final app in list)
              ListRow(
                title: '${app['Name']}',
                leading: AppIcon(app: app, size: 28),
                trailing: [RowIconButton(icon: Icons.delete_outline_rounded, tooltip: 'Remove', onPressed: () => state.call('tiles.remove', {'key': app['Key']}))],
              ),
            const NoteText('Launchpad shows apps pinned in Windows Start, in their order, plus tiles you add manually. Refresh from Launchpad after changing Start pins.'),
          ]);
        },
      ),
    ];

// ---------------------------------------------------------------------- Search

List<Widget> searchPage(BuildContext context, AppState s) => [
      Group(title: 'Shortcut', children: [hotkeyField(s, 'SearchHotkey', 'Open / close search', 'search', 'Ctrl+Alt+K')]),
      Group(title: 'Search apps', children: [s.slider('SearchResultLimit', 'Maximum results', 5, 100)]),
      ButtonRow([PillButton(label: 'Search', icon: Icons.search_rounded, onPressed: () => s.act('search'))]),
    ];

// ---------------------------------------------------------------------- Tiling

List<Widget> tilingPage(BuildContext context, AppState s) {
  final alt = s.setting<String>('TilingModifier', 'Alt') == 'Alt';
  final overrides = ((s.settings['TilingKeybindings'] as Map?) ?? const {}).cast<String, String>();
  final others = [
    for (final k in const ['LaunchpadHotkey', 'SearchHotkey', 'DockFocusHotkey', 'RestoreTaskbarHotkey']) s.setting<String>(k, ''),
  ];
  final defs = tilingShortcuts(altModifier: alt);
  return [
    Group(title: 'Window management', children: [
      s.toggle('TilingEnabled', 'Enable built-in tiling'),
      s.toggle('TilingShowWorkspaces', 'Show workspace buttons on menu bar'),
      s.toggle('TilingHideDock', 'Hide dock while tiled windows are visible'),
      s.toggle('TilingBringActivatedWindows', 'Bring activated apps into the current workspace'),
      s.toggle('TilingTileMaximizedApps', 'Tile apps that start maximized'),
      const NoteText('Automatically arrange resizable apps. Dialogs and fullscreen apps float. Maximizing an already tiled window floats it. Turning this off restores positions.'),
      s.choice('TilingModifier', 'Shortcut modifier', const [('Alt', 'Alt'), ('Ctrl+Alt+Win', 'CtrlAltWin')]),
      s.slider('TilingWorkspaces', 'Workspaces per monitor', 1, 9),
    ]),
    Group(title: 'Layout & motion', children: [
      s.slider('TilingGap', 'Inner gap', 0, 40),
      s.slider('TilingOuterGap', 'Outer gap', 0, 80),
      s.slider('TilingAnimationMs', 'Window animation duration', 0, 600, suffix: ' ms'),
      s.slider('TilingAnimationFps', 'Window animation FPS', 30, 144),
      s.slider('TilingStartupDelayMs', 'Wait for app startup', 0, 500, suffix: ' ms'),
      const NoteText('Wait briefly for startup sizing before tiling. Motion respects Windows animation preferences.'),
    ]),
    Group(title: 'Window borders', children: [
      s.toggle('TilingBorders', 'Add borders to tiled windows'),
      s.text('TilingActiveBorderColor', 'Focused window color', validate: (t) => hexColorError(t) == null ? null : 'Use a color like A8C7FA.', normalize: normalizeHex),
      s.text('TilingInactiveBorderColor', 'Other windows color', validate: (t) => hexColorError(t) == null ? null : 'Use a color like A8C7FA.', normalize: normalizeHex),
      const NoteText('Native borders require Windows 11. Disabling tiling releases the color override.'),
    ]),
    Group(title: 'Rules', children: [
      s.text('TilingExcludedApps', 'Excluded executable names'),
      const NoteText('Separate executable names with semicolons, for example game.exe;Photoshop.exe. FloatingDock windows are always excluded.'),
    ]),
    Group(title: 'Keyboard shortcuts', children: [
      const NoteText('Edit any binding below. Leave it empty to disable that action. Duplicate shortcuts are rejected.'),
      const NoteText('Workspace bindings above your workspace count stay inactive.'),
      Advanced([
        for (final d in defs)
          TextRow(
            label: d.arg == null ? d.label : trf(d.label, [d.arg]),
            value: overrides[d.id] ?? d.defaultValue,
            validate: (text) {
              final candidate = {...overrides, d.id: text};
              return tilingShortcutsError(altModifier: alt, workspaces: 9, overrides: candidate, otherHotkeys: others);
            },
            onChanged: (text) => s.update({'TilingKeybindings': {...overrides, d.id: text.trim()}}),
          ),
      ]),
      ButtonRow([PillButton(label: 'Reset tiling shortcuts', onPressed: () => s.update({'TilingKeybindings': <String, String>{}}))]),
      const NoteText('Inactive workspaces are minimized, so their windows remain accessible through the dock even after an unexpected exit.'),
    ]),
  ];
}

// ---------------------------------------------------------------------- Windows taskbar

List<Widget> taskbarPage(BuildContext context, AppState s) => [
      const NoteText(
          'FloatingDock replaces the taskbar visually. The original Windows taskbar is always put back when FloatingDock exits, crashes, or when you use any of the restore options below.'),
      Group(children: [
        s.choice('TaskbarMode', 'Windows taskbar', const [
          ('Leave as it is', 'Leave'),
          ('Auto-hide', 'AutoHide'),
          ('Hide completely (experimental, also hides tray and clock)', 'HideCompletely'),
        ]),
        s.toggle('ReserveWorkArea', 'Reserve screen space for the dock (windows maximize above it)'),
      ]),
      const NoteText('Space is only reserved while the taskbar is auto-hidden / hidden and the dock does not auto-hide.'),
      Group(title: 'Restore', children: [
        hotkeyField(s, 'RestoreTaskbarHotkey', 'Emergency restore hotkey', 'restoreTaskbar', 'Ctrl+Alt+Shift+T'),
      ]),
      ButtonRow([PillButton(label: 'Restore the Windows taskbar now', icon: Icons.restore_rounded, onPressed: () => s.call('taskbar.restore'))]),
      const NoteText('If everything fails, start  FloatingDock.exe --restore-taskbar  or use Settings → Personalization → Taskbar → Automatically hide.'),
    ];

// ---------------------------------------------------------------------- About

List<Widget> aboutPage(BuildContext context, AppState s) => [
      LiveData<Map?>(
        load: (state) async => await state.call('app.info') as Map?,
        builder: (context, info, state) {
          return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 2),
              child: Text('FloatingDock ${info?['version'] ?? ''}', style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w500)),
            ),
            const NoteText('A floating, macOS-style dock, Launchpad and icon packs for Windows 10 / 11. MIT license.'),
            Group(title: 'Data', children: [
              NoteText('${info?['dataPath'] ?? ''}'),
              ButtonRow([
                PillButton(label: 'Open data folder', icon: Icons.folder_open_rounded, onPressed: () => state.call('shell.open', {'target': 'data'})),
                PillButton(label: 'Open log folder', icon: Icons.description_outlined, onPressed: () => state.call('shell.open', {'target': 'logs'})),
              ]),
            ]),
            Group(title: 'Maintenance', children: [
              ButtonRow([
                PillButton(
                  label: 'Reset all settings to defaults',
                  icon: Icons.settings_backup_restore_rounded,
                  onPressed: () async {
                    final ok = await confirmDialog(context, 'Reset settings',
                        'All dock settings return to their defaults. Pinned apps, Launchpad tiles and icons are kept.', ok: 'Reset');
                    if (ok) await state.call('settings.reset');
                  },
                ),
                PillButton(label: 'Restart FloatingDock', icon: Icons.restart_alt_rounded, onPressed: () => state.call('app.restart')),
                PillButton(label: 'Quit FloatingDock', icon: Icons.power_settings_new_rounded, danger: true, onPressed: () => state.call('app.quit')),
              ]),
            ]),
          ]);
        },
      ),
    ];

// ---------------------------------------------------------------------- Icon packs

List<Widget> iconPacksPage(BuildContext context, AppState s) => [
      Group(children: [
        s.toggle('UseIconPack', 'Use icon packs'),
        s.toggle('PreferDarkVariants', 'Prefer dark variants (name_dark.png)'),
      ]),
      const NoteText('Individual icons set with Change icon take priority. Use Reset icon on an app to let the selected pack choose its icon automatically.'),
      const _PackSection(),
      const _AppIconsSection(),
    ];

class _PackSection extends StatelessWidget {
  const _PackSection();

  @override
  Widget build(BuildContext context) {
    return LiveData<List?>(
      refreshOn: const {'packs.changed'},
      load: (state) async => await state.call('packs.list') as List?,
      builder: (context, data, state) {
        final packs = (data ?? const []).cast<Map>().map((e) => e.cast<String, Object?>()).toList();
        final active = state.setting<String>('ActiveIconPack', 'Default');
        final selected = packs.indexWhere((p) => '${p['folder']}'.toLowerCase() == active.toLowerCase() || '${p['id']}'.toLowerCase() == active.toLowerCase());
        final pack = selected >= 0 ? packs[selected] : null;
        String label(Map p) {
          final id = '${p['id']}';
          if (id == 'Default') return tr('System icons');
          if (id == 'Custom') return '${tr('My icons')}  (${p['iconCount']})';
          return '${p['name']}  (${p['iconCount']} icons)';
        }

        return Group(title: 'Pack', children: [
          if (packs.isNotEmpty)
            ChoiceRow<int>(
              label: 'Active icon pack',
              items: [for (var i = 0; i < packs.length; i++) (label(packs[i]), i)],
              value: selected < 0 ? 0 : selected,
              onChanged: (i) => state.update({'ActiveIconPack': packs[i]['folder'], 'UseIconPack': true}),
            ),
          if (pack != null && '${pack['description']}'.isNotEmpty) NoteText('${pack['description']}'),
          ButtonRow([
            PillButton(label: 'Apply pack to dock applications', icon: Icons.done_all_rounded, onPressed: pack == null ? null : () => state.call('packs.apply', {'folder': pack['folder']})),
          ]),
          const NoteText('Applying a pack resets individual icons only for apps found in the pack.'),
          ButtonRow([
            PillButton(
              label: 'Import ZIP…',
              icon: Icons.file_upload_outlined,
              onPressed: () async {
                final path = await native.pickFile(View.of(context).viewId, filters: const [('Icon pack (ZIP)', '*.zip')]);
                if (path == null || !context.mounted) return;
                try {
                  final preview = await state.call('packs.inspect', {'path': path}) as Map;
                  final problems = (preview['problems'] as List?) ?? const [];
                  if (!context.mounted) return;
                  final ok = await confirmDialog(
                      context,
                      'Import icon pack',
                      '${preview['name']}  v${preview['version']}\n${trf('{0} icons, e.g. {1}', [preview['iconCount'], (preview['iconNames'] as List).take(8).join(', ')])}'
                          '${problems.isEmpty ? '' : '\n\n${tr('Problems:')}\n${problems.take(6).join('\n')}'}',
                      ok: 'Import');
                  if (ok) await state.call('packs.import', {'path': path});
                } catch (e) {
                  if (context.mounted) await messageDialog(context, 'Cannot import', '$e');
                }
              },
            ),
            PillButton(
              label: 'Export active pack…',
              icon: Icons.file_download_outlined,
              onPressed: pack == null
                  ? null
                  : () async {
                      final path = await native.pickFile(View.of(context).viewId, filters: const [('Icon pack (ZIP)', '*.zip')], save: true, fileName: '${pack['name']}.zip');
                      if (path == null || !context.mounted) return;
                      try {
                        await state.call('packs.export', {'folder': pack['folder'], 'path': path});
                        if (context.mounted) await messageDialog(context, 'Export', trf('Saved to {0}', [path]));
                      } catch (e) {
                        if (context.mounted) await messageDialog(context, 'Export failed', '$e');
                      }
                    },
            ),
            PillButton(
              label: 'Delete pack',
              icon: Icons.delete_outline_rounded,
              danger: true,
              onPressed: pack == null
                  ? null
                  : () async {
                      if (pack['isBuiltIn'] == true) {
                        await messageDialog(context, 'Icon packs', tr('Built-in icon folders cannot be deleted. Imported packs can be removed.'));
                        return;
                      }
                      final ok = await confirmDialog(context, 'Delete icon pack', trf('Delete “{0}” and its {1} icons from disk?', [pack['name'], pack['iconCount']]), ok: 'Delete', danger: true);
                      if (ok) await state.call('packs.delete', {'folder': pack['folder']});
                    },
            ),
          ]),
          ButtonRow([
            PillButton(label: 'Open icon folder', icon: Icons.folder_open_rounded, onPressed: () => state.call('shell.open', {'target': 'packs'})),
            PillButton(label: 'Reload', icon: Icons.refresh_rounded, onPressed: () => state.call('packs.reload')),
          ]),
          const NoteText(
              'Drop PNG / ICO files named after the app (e.g. Firefox.png, Visual Studio Code.png) into the Custom folder: they are picked up automatically. The same icons are used by the Dock and the Launchpad.'),
        ]);
      },
    );
  }
}

class _AppIconsSection extends StatelessWidget {
  const _AppIconsSection();

  @override
  Widget build(BuildContext context) {
    return LiveData<List?>(
      refreshOn: const {'icons.changed', 'tiles.changed', 'pinned.changed'},
      load: (state) async => await state.call('appicons.list') as List?,
      builder: (context, data, state) {
        final apps = (data ?? const []).cast<Map>().map((e) => e.cast<String, Object?>()).toList();
        return Group(title: 'Icons of individual apps', children: [
          if (apps.isEmpty) const NoteText('Pin apps to the Dock or add Launchpad tiles to set their icons here.'),
          for (final app in apps)
            ListRow(
              title: '${app['Name']}',
              leading: AppIcon(app: app, size: 28),
              trailing: [
                TextButton(
                  onPressed: () async {
                    final path = await native.pickFile(View.of(context).viewId, filters: const [('Images', '*.png;*.ico')]);
                    if (path != null) await state.call('icons.override', {'key': app['Key'], 'path': path});
                  },
                  child: Text(tr('Change…')),
                ),
                TextButton(
                  onPressed: app['hasOverride'] == true ? () => state.call('icons.reset', {'key': app['Key']}) : null,
                  child: Text(tr('Reset')),
                ),
              ],
            ),
        ]);
      },
    );
  }
}
