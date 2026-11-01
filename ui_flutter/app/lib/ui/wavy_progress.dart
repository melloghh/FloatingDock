import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';

/// The Material 3 expressive progress indicator: the played part is a travelling wave, the rest a flat rounded track.
/// The wave only moves (and the widget only asks for frames) while [playing]; a paused track is a flat line.
class WavyProgress extends StatefulWidget {
  const WavyProgress({super.key, required this.progress, required this.playing, this.height = 26});

  /// Fraction played, 0..1.
  final double progress;
  final bool playing;
  final double height;

  @override
  State<WavyProgress> createState() => _WavyProgressState();
}

class _WavyProgressState extends State<WavyProgress> with SingleTickerProviderStateMixin {
  static const period = 22.0;
  late final Ticker _ticker = createTicker((elapsed) => setState(() => _elapsed = elapsed));
  Duration _elapsed = Duration.zero;

  @override
  void initState() {
    super.initState();
    if (widget.playing) _ticker.start();
  }

  @override
  void didUpdateWidget(WavyProgress old) {
    super.didUpdateWidget(old);
    if (widget.playing && !_ticker.isActive) _ticker.start();
    if (!widget.playing && _ticker.isActive) _ticker.stop();
  }

  @override
  void dispose() {
    _ticker.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    // One wavelength per 1.1 s, like the WinUI version; paused means no phase change and no wave.
    final phase = widget.playing ? (_elapsed.inMicroseconds / 1100000.0 % 1.0) * period : 0.0;
    return SizedBox(
      height: widget.height,
      width: double.infinity,
      child: CustomPaint(
        painter: _WavePainter(
          progress: widget.progress.clamp(0.0, 1.0),
          phase: phase,
          wavy: widget.playing,
          played: cs.primary,
          track: cs.surfaceContainerHighest,
        ),
      ),
    );
  }
}

class _WavePainter extends CustomPainter {
  _WavePainter({required this.progress, required this.phase, required this.wavy, required this.played, required this.track});
  final double progress, phase;
  final bool wavy;
  final Color played, track;

  static const amplitude = 4.2, stroke = 4.0, period = 22.0;

  @override
  void paint(Canvas canvas, Size size) {
    final mid = size.height / 2;
    final playedWidth = math.max(stroke, size.width * progress);
    final trackPaint = Paint()
      ..color = track
      ..strokeWidth = stroke
      ..strokeCap = StrokeCap.round
      ..style = PaintingStyle.stroke;
    final start = math.min(size.width, playedWidth + 4);
    if (start < size.width) canvas.drawLine(Offset(start, mid), Offset(size.width, mid), trackPaint);

    final wavePaint = Paint()
      ..color = played
      ..strokeWidth = stroke
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round
      ..style = PaintingStyle.stroke;
    if (!wavy) {
      canvas.drawLine(Offset(0, mid), Offset(playedWidth, mid), wavePaint);
    } else {
      // Half-periods as cubic arcs, shifted left by the phase and clipped to the played width.
      canvas.save();
      canvas.clipRect(Rect.fromLTWH(-stroke, 0, playedWidth + stroke, size.height));
      final path = Path()..moveTo(-phase, mid);
      final half = period / 2;
      final halves = ((playedWidth + phase) / half).ceil() + 1;
      for (var i = 0; i < halves; i++) {
        final x = i * half - phase;
        final dir = i.isEven ? -1.0 : 1.0;
        path.cubicTo(x + half * .36, mid + dir * amplitude * 1.33, x + half * .64, mid + dir * amplitude * 1.33, x + half, mid);
      }
      canvas.drawPath(path, wavePaint);
      canvas.restore();
    }
    canvas.drawCircle(Offset(math.min(size.width - 4, playedWidth), mid), 4, Paint()..color = played);
  }

  @override
  bool shouldRepaint(_WavePainter old) =>
      old.progress != progress || old.phase != phase || old.wavy != wavy || old.played != played || old.track != track;
}
