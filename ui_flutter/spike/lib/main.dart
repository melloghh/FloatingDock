import 'dart:async';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';

/// Spike: transparent borderless window, the panel pops out of its anchor.
/// args[0] = optional path of a log file with frame timings.
void main(List<String> args) {
  WidgetsFlutterBinding.ensureInitialized();
  final log = args.isNotEmpty ? File(args[0]) : null;
  runWidget(Views(log: log));
}

/// One engine, many views: every native window the runner adds becomes a View here.
class Views extends StatefulWidget {
  const Views({super.key, this.log});
  final File? log;
  @override
  State<Views> createState() => _ViewsState();
}

class _ViewsState extends State<Views> with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    // Idle diagnostics: how many frames and metrics callbacks happen between 8 s and 13 s.
    Timer(const Duration(seconds: 8), () {
      final m0 = metricsCalls;
      final stamps = <int>[];
      SchedulerBinding.instance.addTimingsCallback((t) {
        idleFrames += t.length;
        for (final f in t) {
          stamps.add(f.timestampInMicroseconds(ui.FramePhase.vsyncStart));
        }
      });
      Timer(const Duration(seconds: 6), () {
        final deltas = <String>[];
        for (var i = 1; i < stamps.length && i < 40; i++) {
          deltas.add(((stamps[i] - stamps[i - 1]) / 1000).toStringAsFixed(0));
        }
        widget.log?.writeAsStringSync('idle 8s..14s: frames~$idleFrames metricsCalls=${metricsCalls - m0} '
            'views=${WidgetsBinding.instance.platformDispatcher.views.length}\n'
            'frame deltas ms: ${deltas.join(' ')}\n', mode: FileMode.append);
      });
    });
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  int metricsCalls = 0, idleFrames = 0;

  @override
  void didChangeMetrics() {
    metricsCalls++;
    setState(() {});
  }

  @override
  Widget build(BuildContext context) => ViewCollection(views: [
        for (final v in WidgetsBinding.instance.platformDispatcher.views)
          View(view: v, child: SpikeApp(log: v.viewId == 0 ? widget.log : null)),
      ]);
}

class SpikeApp extends StatelessWidget {
  const SpikeApp({super.key, this.log});
  final File? log;

  @override
  Widget build(BuildContext context) => MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: ThemeData(
          useMaterial3: true,
          colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF4E9F75), brightness: Brightness.dark),
          scaffoldBackgroundColor: Colors.transparent,
          canvasColor: Colors.transparent,
        ),
        home: PopHost(log: log),
      );
}

class PopHost extends StatefulWidget {
  const PopHost({super.key, this.log});
  final File? log;
  @override
  State<PopHost> createState() => _PopHostState();
}

class _PopHostState extends State<PopHost> with SingleTickerProviderStateMixin {
  // ~400 ms: scale 0.84 -> 1.04 -> 1, rise 14 dip, fade.
  late final AnimationController _c =
      AnimationController(vsync: this, duration: const Duration(milliseconds: 400));
  final _timings = <ui.FrameTiming>[];

  @override
  void initState() {
    super.initState();
    SchedulerBinding.instance.addTimingsCallback((t) => _timings.addAll(t));
    _c.addStatusListener((s) {
      if (s == AnimationStatus.completed) _report();
    });
    Future.delayed(const Duration(milliseconds: 300), pop);
    // Second pop after an idle stretch so the log has a warm run too.
    Timer(const Duration(seconds: 4), pop);
  }

  void pop() {
    _timings.clear();
    _c.forward(from: 0);
  }

  void _report() {
    // Frame timings reach Dart in batches, so give them time to arrive.
    Future.delayed(const Duration(milliseconds: 1500), () {
      final log = widget.log;
      if (log == null || _timings.isEmpty) return;
      double ms(Duration d) => d.inMicroseconds / 1000.0;
      final build = _timings.map((t) => ms(t.buildDuration)).reduce((a, b) => a > b ? a : b);
      final raster = _timings.map((t) => ms(t.rasterDuration)).reduce((a, b) => a > b ? a : b);
      final total = _timings.map((t) => ms(t.totalSpan)).reduce((a, b) => a > b ? a : b);
      log.writeAsStringSync(
          'pop frames=${_timings.length} maxBuild=${build.toStringAsFixed(2)}ms '
          'maxRaster=${raster.toStringAsFixed(2)}ms maxTotal=${total.toStringAsFixed(2)}ms\n',
          mode: FileMode.append);
    });
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final scale = TweenSequence<double>([
      TweenSequenceItem(tween: Tween(begin: .84, end: 1.04).chain(CurveTween(curve: Curves.easeOutCubic)), weight: 65),
      TweenSequenceItem(tween: Tween(begin: 1.04, end: 1.0).chain(CurveTween(curve: Curves.easeInOut)), weight: 35),
    ]).animate(_c);
    final fade = CurvedAnimation(parent: _c, curve: const Interval(0, .7, curve: Curves.easeOut));
    final rise = Tween(begin: 14.0, end: 0.0).animate(CurvedAnimation(parent: _c, curve: Curves.easeOutCubic));

    return ColoredBox(
      // Half-transparent magenta over the whole window proves per-pixel alpha reaches the desktop.
      color: Platform.environment['FD_BG'] == '1' ? const Color(0x80FF00FF) : Colors.transparent,
      child: GestureDetector(
      onTap: pop,
      behavior: HitTestBehavior.translucent,
      child: Padding(
        padding: const EdgeInsets.all(28),
        child: AnimatedBuilder(
          animation: _c,
          builder: (context, child) => Opacity(
            opacity: fade.value,
            child: Transform.translate(
              offset: Offset(0, rise.value),
              child: Transform.scale(scale: scale.value, alignment: Alignment.topCenter, child: child),
            ),
          ),
          child: Container(
            height: 56,
            decoration: BoxDecoration(
              color: cs.surfaceContainerHigh,
              borderRadius: BorderRadius.circular(28),
              boxShadow: const [BoxShadow(blurRadius: 24, offset: Offset(0, 8), color: Color(0x66000000))],
            ),
            alignment: Alignment.center,
            child: Row(mainAxisSize: MainAxisSize.min, children: [
              Icon(Icons.search, color: cs.onSurfaceVariant),
              const SizedBox(width: 12),
              Text('Spike: pop + прозрачность', style: TextStyle(color: cs.onSurface, fontSize: 16)),
            ]),
          ),
        ),
      ),
      ),
    );
  }
}
