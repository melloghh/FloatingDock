import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import '../../core/app_state.dart';
import '../../core/sfx.dart';
import '../../ui/motion.dart';
import 'pages.dart';

/// The settings window: a rounded navigation rail on the left with one highlight that glides between pages, the page
/// on the surface to its right. Pages reveal with rise, fade and a sharpening blur.
class SettingsView extends StatefulWidget {
  const SettingsView({super.key, this.initialPage = 'Dock', this.navigate});
  final String initialPage;

  /// Asks the open window to show another page (set by the controller when settings are opened with a section).
  final ValueListenable<String?>? navigate;

  @override
  State<SettingsView> createState() => _SettingsViewState();
}

class _SettingsViewState extends State<SettingsView> {
  late String _page = widget.initialPage;
  int _revision = 0;

  @override
  void initState() {
    super.initState();
    widget.navigate?.addListener(_onNavigate);
  }

  @override
  void dispose() {
    widget.navigate?.removeListener(_onNavigate);
    super.dispose();
  }

  void _onNavigate() {
    final page = widget.navigate?.value;
    if (page != null && settingsPages.any((p) => p.name == page)) select(page);
  }

  void select(String name) {
    if (name == _page) return;
    setState(() {
      _page = name;
      _revision++;
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = AppScope.of(context);
    final cs = Theme.of(context).colorScheme;
    final def = settingsPages.firstWhere((p) => p.name == _page, orElse: () => settingsPages.first);
    final index = settingsPages.indexOf(def);
    // The reference: a lighter navigation column running the full height on the left, the near-black page to its right,
    // the current page marked by one lavender pill with dark text.
    return Scaffold(
      backgroundColor: cs.surface,
      body: Row(children: [
        Container(
          width: 248,
          padding: const EdgeInsets.fromLTRB(12, 16, 12, 12),
          color: cs.surfaceContainerLow,
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 2, 0, 16),
              child: Text('FloatingDock', style: TextStyle(fontSize: 14, fontWeight: FontWeight.w700, color: cs.onSurface)),
            ),
            Expanded(
              child: SingleChildScrollView(
                child: GlideList(
                  count: settingsPages.length,
                  selected: index,
                  itemExtent: 40,
                  gap: 2,
                  radius: 20,
                  highlight: cs.primary,
                  itemBuilder: (context, i, selected) {
                    final p = settingsPages[i];
                    final color = selected ? cs.onPrimary : cs.onSurfaceVariant;
                    return Material(
                      color: Colors.transparent,
                      shape: const StadiumBorder(),
                      clipBehavior: Clip.antiAlias,
                      child: InkWell(
                        onTap: () {
                          Sfx.play(Sound.tap);
                          select(p.name);
                        },
                        child: Padding(
                          padding: const EdgeInsets.symmetric(horizontal: 14),
                          child: Row(children: [
                            Icon(p.icon, size: 18, color: color),
                            const SizedBox(width: 12),
                            Expanded(
                              child: AnimatedDefaultTextStyle(
                                duration: const Duration(milliseconds: 200),
                                style: Theme.of(context).textTheme.bodyMedium!.copyWith(fontSize: 12.5, color: color, fontWeight: selected ? FontWeight.w700 : FontWeight.w500),
                                child: Text(tr(p.name), overflow: TextOverflow.ellipsis),
                              ),
                            ),
                          ]),
                        ),
                      ),
                    );
                  },
                ),
              ),
            ),
          ]),
        ),
        Expanded(
          child: Scrollbar(
            child: SingleChildScrollView(
              key: PageStorageKey<String>('page-$_page'),
              padding: const EdgeInsets.fromLTRB(28, 24, 32, 40),
              child: Align(
                alignment: Alignment.topLeft,
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 860),
                  child: Reveal(
                    replayKey: _revision,
                    child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                      Padding(
                        padding: const EdgeInsets.fromLTRB(16, 0, 16, 6),
                        child: Text(tr(def.name), style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700, color: cs.onSurface)),
                      ),
                      ...def.build(context, s),
                    ]),
                  ),
                ),
              ),
            ),
          ),
        ),
      ]),
    );
  }
}
