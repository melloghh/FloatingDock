import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';

import '../../core/app_state.dart';
import '../../core/theme.dart';
import '../../ui/glide_scope.dart';
import '../../ui/wavy_progress.dart';
import 'popup_view.dart';

/// Panel width per menu, logical pixels.
double menuWidth(String kind) => kind == 'calendar' ? 364 : 334;

/// The menu for [kind] (main, media, sound, network, language, calendar, tray).
Widget menuFor(String kind) => switch (kind) {
      'main' => const MainMenu(),
      'media' => const MediaMenu(),
      'sound' => const SoundMenu(),
      'network' => const NetworkMenu(),
      'language' => LanguageMenu(target: _languageTarget),
      'calendar' => const CalendarMenu(),
      'tray' => const TrayMenu(),
      _ => const SizedBox.shrink(),
    };

/// The window the keyboard layout menu was opened for (the one the user was typing in); set by the controller.
int? _languageTarget;
void setLanguageTarget(int? hwnd) => _languageTarget = hwnd;

void _openWindowsPage(BuildContext context, String uri) {
  AppScope.read(context).call('shell.execute', {'uri': uri});
  PopupScope.of(context).close();
}

// ---------------------------------------------------------------------- main menu

class MainMenu extends StatefulWidget {
  const MainMenu({super.key});
  @override
  State<MainMenu> createState() => _MainMenuState();
}

class _MainMenuState extends State<MainMenu> {
  String? _confirm; // 'restart' | 'shutdown' while asking

  @override
  Widget build(BuildContext context) {
    final s = AppScope.of(context);
    final popup = PopupScope.of(context);
    final cs = Theme.of(context).colorScheme;
    if (_confirm != null) {
      final restart = _confirm == 'restart';
      return Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(4, 4, 4, 8),
          child: Text(tr(restart ? 'Restart this PC?' : 'Shut down this PC?'), style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w500)),
        ),
        MenuNote('Save your work before continuing.'),
        const SizedBox(height: 12),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          TextButton(autofocus: true, onPressed: () => setState(() => _confirm = null), child: Text(tr('Cancel'))),
          const SizedBox(width: 8),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: cs.error, foregroundColor: cs.onError),
            onPressed: () {
              s.call('power.run', {'action': restart ? 'restart' : 'shutdown'});
              popup.close();
            },
            child: Text(tr(restart ? 'Restart' : 'Shut down')),
          ),
        ]),
      ]);
    }
    void act(String name) {
      popup.close();
      s.act(name);
    }

    return GlideScope(
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        MenuRow(title: 'Search', icon: Icons.search_rounded, onTap: () => act('search')),
        const SizedBox(height: 5),
        MenuRow(title: 'Launchpad', icon: Icons.apps_rounded, onTap: () => act('launchpad')),
        const SizedBox(height: 5),
        MenuRow(title: 'FloatingDock settings', icon: Icons.settings_rounded, onTap: () => act('settings')),
        const MenuDivider(),
        MenuRow(
          title: 'Auto-hide menu bar',
          icon: Icons.vertical_align_top_rounded,
          selected: s.setting<bool>('TopBarAutoHide', false),
          onTap: () {
            s.update({'TopBarAutoHide': !s.setting<bool>('TopBarAutoHide', false)});
            popup.close();
          },
        ),
        const SizedBox(height: 5),
        MenuRow(
          title: 'Hide menu bar',
          icon: Icons.visibility_off_outlined,
          onTap: () {
            s.update({'TopBarEnabled': false});
            popup.close();
          },
        ),
        const MenuDivider(),
        MenuRow(title: 'Restart', icon: Icons.restart_alt_rounded, onTap: () => setState(() => _confirm = 'restart')),
        const SizedBox(height: 5),
        MenuRow(title: 'Shut down', icon: Icons.power_settings_new_rounded, onTap: () => setState(() => _confirm = 'shutdown')),
        const MenuDivider(),
        MenuRow(title: 'Quit FloatingDock', icon: Icons.logout_rounded, onTap: () => s.call('app.quit')),
      ]),
    );
  }
}

// ---------------------------------------------------------------------- media

class MediaMenu extends StatefulWidget {
  const MediaMenu({super.key});
  @override
  State<MediaMenu> createState() => _MediaMenuState();
}

class _MediaMenuState extends State<MediaMenu> {
  Map? _media;
  bool _loaded = false, _failed = false, _acting = false;
  DateTime _readAt = DateTime.now();
  Timer? _poll, _tick;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _read());
    _poll = Timer.periodic(const Duration(milliseconds: 700), (_) => _read());
    // Between reads the position advances locally, so the time and the wave never freeze.
    _tick = Timer.periodic(const Duration(milliseconds: 250), (_) {
      if (mounted && _media?['playing'] == true) setState(() {});
    });
  }

  @override
  void dispose() {
    _poll?.cancel();
    _tick?.cancel();
    _followUp?.cancel();
    super.dispose();
  }

  Future<void> _read() async {
    if (_acting || !mounted) return;
    try {
      final m = await AppScope.read(context).call('media.get') as Map?;
      if (!mounted) return;
      setState(() {
        _media = m;
        _loaded = true;
        _failed = false;
        _readAt = DateTime.now();
      });
    } catch (_) {
      if (mounted) {
        setState(() {
          _loaded = true;
          _failed = true;
        });
      }
    }
  }

  Future<void> _act(String command) async {
    if (_acting) return;
    // The button answers at once; the player confirms a moment later.
    final current = _media;
    if (command == 'toggle' && current != null) {
      setState(() {
        final now = (current['position'] as num?)?.toDouble() ?? 0;
        final moved = current['playing'] == true ? DateTime.now().difference(_readAt).inMilliseconds / 1000 : 0;
        _media = {...current, 'position': now + moved, 'playing': current['playing'] != true};
        _readAt = DateTime.now();
      });
    }
    _acting = true;
    try {
      await AppScope.read(context).call('media.control', {'command': command});
    } catch (_) {
      if (mounted) setState(() => _failed = true);
    }
    _acting = false;
    await _read();
    _followUp?.cancel();
    _followUp = Timer(const Duration(milliseconds: 350), _read);   // players confirm a moment after the command
  }

  Timer? _followUp;

  static String clock(double seconds) {
    final t = Duration(seconds: seconds.floor().clamp(0, 359999));
    final s = t.inSeconds % 60;
    return t.inHours >= 1 ? '${t.inHours}:${(t.inMinutes % 60).toString().padLeft(2, '0')}:${s.toString().padLeft(2, '0')}' : '${t.inMinutes}:${s.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final m = _media;
    final playing = m?['playing'] == true;
    final duration = (m?['duration'] as num?)?.toDouble() ?? 0;
    var position = (m?['position'] as num?)?.toDouble() ?? 0;
    if (playing) position += DateTime.now().difference(_readAt).inMilliseconds / 1000;
    if (duration > 0) position = position.clamp(0, duration);
    final title = _failed ? tr('Media controls are unavailable.') : !_loaded ? tr('Searching…') : m == null ? tr('No active media session') : '${m['title']}';

    Widget round(IconData icon, String label, bool enabled, String command, {double w = 44, double h = 44, bool primary = false, bool active = false, double size = 20}) => Tooltip(
          message: tr(label),
          child: SizedBox(
            width: w,
            height: h,
            child: Material(
              color: primary ? cs.primary : active ? cs.secondaryContainer : cs.surfaceContainerHighest,
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(h / 2)),
              clipBehavior: Clip.antiAlias,
              child: InkWell(
                onTap: enabled ? () => _act(command) : null,
                child: Icon(icon, size: size, color: (primary ? cs.onPrimary : active ? cs.primary : cs.onSurface).withValues(alpha: enabled ? 1 : .35)),
              ),
            ),
          ),
        );

    final repeat = (m?['repeat'] as num?)?.toInt() ?? 0;
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      Row(children: [
        Container(
          width: 64,
          height: 64,
          decoration: BoxDecoration(color: cs.secondaryContainer, borderRadius: BorderRadius.circular(Shapes.l)),
          child: Icon(Icons.music_note_rounded, size: 28, color: cs.onSecondaryContainer),
        ),
        const SizedBox(width: 14),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(title, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600), maxLines: 1, overflow: TextOverflow.ellipsis),
            if ('${m?['artist'] ?? ''}'.isNotEmpty)
              Text('${m?['artist']}', style: TextStyle(fontSize: 13, color: cs.onSurfaceVariant), maxLines: 1, overflow: TextOverflow.ellipsis),
          ]),
        ),
      ]),
      const SizedBox(height: 16),
      Row(mainAxisAlignment: MainAxisAlignment.center, children: [
        round(Icons.shuffle_rounded, 'Shuffle', m?['canShuffle'] == true, 'shuffle', active: m?['shuffle'] == true),
        const SizedBox(width: 8),
        round(Icons.skip_previous_rounded, 'Previous', m?['canPrevious'] == true, 'previous', w: 48, h: 48, size: 24),
        const SizedBox(width: 8),
        round(playing ? Icons.pause_rounded : Icons.play_arrow_rounded, 'Play / pause', m?['canToggle'] == true, 'toggle', w: 84, h: 56, primary: true, size: 30),
        const SizedBox(width: 8),
        round(Icons.skip_next_rounded, 'Next', m?['canNext'] == true, 'next', w: 48, h: 48, size: 24),
        const SizedBox(width: 8),
        round(repeat == 1 ? Icons.repeat_one_rounded : Icons.repeat_rounded, 'Repeat', m?['canRepeat'] == true, 'repeat', active: repeat != 0),
      ]),
      const SizedBox(height: 14),
      Row(children: [
        Text(clock(position), style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant)),
        const SizedBox(width: 10),
        Expanded(child: WavyProgress(progress: duration > 0 ? position / duration : 0, playing: playing && duration > 0)),
        const SizedBox(width: 10),
        SizedBox(width: 34, child: Text(duration > 0 ? clock(duration) : '', textAlign: TextAlign.end, style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant))),
      ]),
    ]);
  }
}

// ---------------------------------------------------------------------- sound

class SoundMenu extends StatefulWidget {
  const SoundMenu({super.key});
  @override
  State<SoundMenu> createState() => _SoundMenuState();
}

class _SoundMenuState extends State<SoundMenu> {
  double _volume = 0;
  bool _muted = false, _ok = true, _dragging = false;
  Timer? _poll, _send;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _read());
    _poll = Timer.periodic(const Duration(milliseconds: 700), (_) => _read());
  }

  @override
  void dispose() {
    _poll?.cancel();
    _send?.cancel();
    super.dispose();
  }

  Future<void> _read() async {
    if (_dragging || !mounted) return;
    try {
      final a = await AppScope.read(context).call('audio.get') as Map;
      if (mounted && !_dragging) {
        setState(() {
          _volume = ((a['volume'] as num) * 100).roundToDouble();
          _muted = a['muted'] == true;
          _ok = true;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _ok = false);
    }
  }

  void _setVolume(double v) {
    setState(() => _volume = v);
    _send?.cancel();
    _send = Timer(const Duration(milliseconds: 40), () => AppScope.read(context).call('audio.set', {'volume': v / 100}).catchError((_) => null));
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      const MenuHeader('Sound', Icons.volume_up_rounded),
      Text(_ok ? '${_volume.round()}%' : '—', style: const TextStyle(fontSize: 27, fontWeight: FontWeight.w600)),
      MenuNote('Output device volume'),
      Slider(
        year2023: false, // ignore: deprecated_member_use
        value: _volume.clamp(0, 100),
        min: 0,
        max: 100,
        onChangeStart: (_) => _dragging = true,
        onChangeEnd: (_) {
          _dragging = false;
        },
        onChanged: _ok ? _setVolume : null,
      ),
      const SizedBox(height: 4),
      FilledButton(
        style: FilledButton.styleFrom(
          minimumSize: const Size(0, 40),
          backgroundColor: _muted ? cs.primary : cs.secondaryContainer,
          foregroundColor: _muted ? cs.onPrimary : cs.onSecondaryContainer,
        ),
        onPressed: _ok
            ? () {
                setState(() => _muted = !_muted);
                AppScope.read(context).call('audio.set', {'muted': _muted}).catchError((_) => null);
              }
            : null,
        child: Text(tr(_muted ? 'Unmute' : 'Mute')),
      ),
      if (!_ok) MenuNote('Output device unavailable.'),
      const MenuDivider(),
      GlideScope(child: MenuRow(title: 'Sound devices', detail: tr('More settings'), icon: Icons.settings_rounded, onTap: () => _openWindowsPage(context, 'ms-settings:sound'))),
    ]);
  }
}

// ---------------------------------------------------------------------- language

class LanguageMenu extends StatefulWidget {
  const LanguageMenu({super.key, this.target});
  final int? target;
  @override
  State<LanguageMenu> createState() => _LanguageMenuState();
}

class _LanguageMenuState extends State<LanguageMenu> {
  Map? _data;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      final r = await AppScope.read(context).call('language.list', {if (widget.target != null) 'target': widget.target}) as Map;
      if (mounted) setState(() => _data = r);
    });
  }

  @override
  Widget build(BuildContext context) {
    final layouts = ((_data?['layouts'] as List?) ?? const []).cast<Map>();
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      const MenuHeader('Input language', Icons.language_rounded),
      MenuNote('Current application keyboard layout'),
      const SizedBox(height: 8),
      if (_data != null && layouts.isEmpty) MenuNote('No keyboard layouts available'),
      GlideScope(
        child: Column(children: [
          for (final l in layouts)
            Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: MenuRow(
                title: '${l['name']}',
                detail: '${l['tag']}',
                icon: Icons.keyboard_rounded,
                translate: false,
                selected: l['current'] == true,
                onTap: () {
                  final s = AppScope.read(context);
                  PopupScope.of(context).close();
                  s.call('language.set', {'target': _data!['target'], 'handle': l['handle']});
                },
              ),
            ),
        ]),
      ),
      const MenuDivider(),
      MenuNote('You can also switch using Win + Space'),
    ]);
  }
}

// ---------------------------------------------------------------------- network

class NetworkMenu extends StatefulWidget {
  const NetworkMenu({super.key});
  @override
  State<NetworkMenu> createState() => _NetworkMenuState();
}

class _NetworkMenuState extends State<NetworkMenu> {
  Map? _data;
  String _status = '';
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _refresh(scan: true));
  }

  Future<void> _refresh({required bool scan}) async {
    setState(() {
      _status = tr('Refreshing…');
      _loading = true;
    });
    try {
      final r = await AppScope.read(context).call('network.get', {'scan': scan}) as Map;
      if (!mounted) return;
      setState(() {
        _data = r;
        _status = '${r['error'] ?? ''}';
        _loading = false;
      });
    } catch (e) {
      if (mounted) {
        setState(() {
          _status = '${tr('Could not get networks: ')}$e';
          _loading = false;
        });
      }
    }
  }

  Future<void> _toggle(Map network) async {
    final s = AppScope.read(context);
    setState(() => _status = tr(network['connected'] == true ? 'Disconnecting…' : 'Connecting…'));
    try {
      final r = await s.call('network.toggle', {'name': network['name']}) as Map;
      if (r['needsWindows'] == true && mounted) {
        _openWindowsPage(context, 'ms-availablenetworks:');
        return;
      }
      await Future<void>.delayed(const Duration(milliseconds: 1200));
      if (mounted) await _refresh(scan: false);
    } catch (e) {
      if (mounted) setState(() => _status = '${tr('Failed: ')}$e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final adapters = ((_data?['adapters'] as List?) ?? const []).cast<Map>();
    final networks = ((_data?['networks'] as List?) ?? const []).cast<Map>();
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      const MenuHeader('Network', Icons.wifi_rounded),
      Text(_data == null ? tr('Checking connection…') : adapters.isNotEmpty ? tr('Connected') : tr('No active connection'),
          style: TextStyle(fontSize: 13, color: cs.onSurfaceVariant)),
      const SizedBox(height: 8),
      for (final a in adapters)
        Container(
          margin: const EdgeInsets.only(bottom: 5),
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(color: cs.secondaryContainer, borderRadius: BorderRadius.circular(Shapes.l)),
          child: Text('${a['kind']} · ${a['name']}', style: TextStyle(fontSize: 13, color: cs.onSecondaryContainer)),
        ),
      if (networks.isNotEmpty) ...[
        const MenuDivider(),
        Padding(padding: const EdgeInsets.fromLTRB(4, 0, 0, 6), child: Text('WI-FI', style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: cs.primary))),
      ],
      GlideScope(
        child: Column(children: [
          for (final n in networks)
            Padding(
              padding: const EdgeInsets.only(bottom: 5),
              child: MenuRow(
                title: '${n['name']}',
                translate: false,
                detail: n['available'] != true
                    ? tr('Saved network · connect')
                    : n['connected'] == true
                        ? '${tr('Connected · ')}${n['signal']}%'
                        : '${tr(n['secured'] == true ? 'Secured network' : 'Open network')} · ${n['signal']}%',
                icon: Icons.wifi_rounded,
                selected: n['connected'] == true,
                onTap: () => _toggle(n),
              ),
            ),
        ]),
      ),
      if (_data != null && networks.isEmpty && '${_data!['error'] ?? ''}'.isEmpty) MenuNote('No Wi-Fi networks found. If scanning is still running, choose Refresh networks.'),
      if (_status.isNotEmpty && !_loading) MenuNote(_status, translate: false),
      const MenuDivider(),
      GlideScope(
        child: Column(children: [
          MenuRow(title: 'Refresh networks', detail: tr('Find nearby Wi-Fi'), icon: Icons.refresh_rounded, onTap: () => _refresh(scan: true)),
          const SizedBox(height: 5),
          MenuRow(
              title: 'Windows networks',
              detail: tr('Connect to a new network with a password'),
              icon: Icons.settings_ethernet_rounded,
              onTap: () => _openWindowsPage(context, 'ms-availablenetworks:')),
        ]),
      ),
    ]);
  }
}

// ---------------------------------------------------------------------- calendar

class CalendarMenu extends StatelessWidget {
  const CalendarMenu({super.key});

  @override
  Widget build(BuildContext context) {
    final now = DateTime.now();
    final cs = Theme.of(context).colorScheme;
    const months = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
    const days = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      const MenuHeader('Calendar', Icons.calendar_month_rounded),
      Padding(
        padding: const EdgeInsets.only(left: 4, bottom: 6),
        child: Text('${tr(days[now.weekday - 1])}, ${now.day} ${tr(months[now.month - 1])} ${now.year}', style: TextStyle(fontSize: 12, color: cs.onSurfaceVariant)),
      ),
      Theme(
        data: Theme.of(context).copyWith(datePickerTheme: DatePickerThemeData(backgroundColor: Colors.transparent, surfaceTintColor: Colors.transparent, dayShape: WidgetStatePropertyAll(const CircleBorder()))),
        child: SizedBox(
          height: 330,
          child: CalendarDatePicker(initialDate: now, firstDate: DateTime(2000), lastDate: DateTime(2100), onDateChanged: (_) {}),
        ),
      ),
      const MenuDivider(),
      GlideScope(child: MenuRow(title: 'Date and time', detail: tr('More settings'), icon: Icons.settings_rounded, onTap: () => _openWindowsPage(context, 'ms-settings:dateandtime'))),
    ]);
  }
}

// ---------------------------------------------------------------------- tray

class TrayMenu extends StatefulWidget {
  const TrayMenu({super.key});
  @override
  State<TrayMenu> createState() => _TrayMenuState();
}

class _TrayMenuState extends State<TrayMenu> {
  List<Map>? _items;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      try {
        final r = await AppScope.read(context).call('tray.items') as Map;
        if (mounted) setState(() => _items = (r['items'] as List).cast<Map>());
      } catch (_) {
        if (mounted) setState(() => _items = const []);
      }
    });
  }

  Future<void> _activate(Map item, bool context_, Offset? global) async {
    final s = AppScope.read(context);
    final popup = PopupScope.of(context);
    if (!context_) popup.close();
    await s.call('tray.activate', {
      'index': item['index'],
      'context': context_,
      if (global != null) 'x': global.dx.round(),
      if (global != null) 'y': global.dy.round(),
    });
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final items = _items;
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      const MenuHeader('System tray', Icons.expand_more_rounded),
      GridView.count(
        shrinkWrap: true,
        physics: const NeverScrollableScrollPhysics(),
        crossAxisCount: 6,
        mainAxisSpacing: 8,
        crossAxisSpacing: 8,
        children: [
          for (final item in items ?? const <Map>[])
            Tooltip(
              message: '${item['name']}',
              child: GestureDetector(
                onSecondaryTapUp: (d) => _activate(item, true, d.globalPosition),
                child: Material(
                  color: cs.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(14),
                  clipBehavior: Clip.antiAlias,
                  child: InkWell(
                    onTap: () => _activate(item, false, null),
                    child: Center(
                      child: item['icon'] is String
                          ? Image.memory(base64Decode(item['icon'] as String), width: 24, height: 24, filterQuality: FilterQuality.medium, gaplessPlayback: true)
                          : Icon(Icons.extension_rounded, size: 20, color: cs.onSurface),
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
      const SizedBox(height: 8),
      MenuNote(items == null ? 'Loading tray icons…' : items.isEmpty ? 'No tray icons available. Keep Explorer running.' : 'Click to open · Right-click for options'),
    ]);
  }
}
