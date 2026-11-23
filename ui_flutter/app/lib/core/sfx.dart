import 'dart:math' as math;
import 'dart:typed_data';

import 'native.dart';

/// The interface sounds: soft water-drop blips, made here from a few sine sweeps (no files, no audio package) and played by the native
/// side from memory. Playing costs one method call; nothing runs while it is silent.
enum Sound { tap, pop, open, close, on, off }

abstract final class Sfx {
  static const _rate = 44100;
  /// Asked on every play: the setting "Interface sounds".
  static bool Function() enabledOf = () => true;
  static bool _ready = false;
  static final _last = <Sound, int>{};

  /// Makes and registers the sounds (a few milliseconds of work, once).
  static void init() {
    if (_ready) return;
    _ready = true;
    for (final s in Sound.values) {
      native.registerSound(s.index, _wav(_render(s))).catchError((_) {});
    }
  }

  /// Plays [sound] unless sounds are off or the same one has just played.
  static void play(Sound sound) {
    if (!_ready || !enabledOf()) return;
    final now = DateTime.now().millisecondsSinceEpoch;
    if (now - (_last[sound] ?? 0) < 70) return;
    _last[sound] = now;
    native.playSound(sound.index).catchError((_) {});
  }

  /// The WAV of [sound] (for tests).
  static Uint8List debugWav(Sound sound) => _wav(_render(sound));

  /// One bubble: a sine that sweeps from [from] to [to] Hz in [ms], with a quick attack and an exponential fade.
  static List<double> _bubble(double from, double to, int ms, {double gain = .32, double shine = .25}) {
    final n = _rate * ms ~/ 1000;
    final out = List<double>.filled(n, 0);
    var phase = 0.0;
    for (var i = 0; i < n; i++) {
      final t = i / n;
      final f = from + (to - from) * (1 - math.pow(1 - t, 2));
      phase += 2 * math.pi * f / _rate;
      final attack = math.min(1.0, i / (_rate * .004));
      final decay = math.exp(-5.5 * t);
      out[i] = gain * attack * decay * (math.sin(phase) + shine * math.sin(2 * phase));
    }
    return out;
  }

  static List<double> _mix(List<List<double>> parts, List<int> startsMs) {
    var length = 0;
    for (var i = 0; i < parts.length; i++) {
      length = math.max(length, _rate * startsMs[i] ~/ 1000 + parts[i].length);
    }
    final out = List<double>.filled(length, 0);
    for (var i = 0; i < parts.length; i++) {
      final at = _rate * startsMs[i] ~/ 1000;
      for (var j = 0; j < parts[i].length; j++) {
        out[at + j] += parts[i][j];
      }
    }
    return out;
  }

  static List<double> _render(Sound s) => switch (s) {
        Sound.tap => _bubble(520, 880, 70),
        Sound.pop => _bubble(330, 760, 90, gain: .36),
        Sound.open => _mix([_bubble(420, 760, 80), _bubble(640, 1040, 110, gain: .26)], [0, 55]),
        Sound.close => _mix([_bubble(900, 560, 80, gain: .28), _bubble(620, 380, 100, gain: .2)], [0, 50]),
        Sound.on => _mix([_bubble(540, 900, 70), _bubble(760, 1220, 100, gain: .3)], [0, 60]),
        Sound.off => _mix([_bubble(900, 600, 70, gain: .28), _bubble(640, 400, 100, gain: .22)], [0, 60]),
      };

  /// 16-bit mono PCM in a WAV container.
  static Uint8List _wav(List<double> samples) {
    final data = ByteData(44 + samples.length * 2);
    void text(int at, String s) {
      for (var i = 0; i < s.length; i++) {
        data.setUint8(at + i, s.codeUnitAt(i));
      }
    }

    text(0, 'RIFF');
    data.setUint32(4, 36 + samples.length * 2, Endian.little);
    text(8, 'WAVEfmt ');
    data.setUint32(16, 16, Endian.little);
    data.setUint16(20, 1, Endian.little);
    data.setUint16(22, 1, Endian.little);
    data.setUint32(24, _rate, Endian.little);
    data.setUint32(28, _rate * 2, Endian.little);
    data.setUint16(32, 2, Endian.little);
    data.setUint16(34, 16, Endian.little);
    text(36, 'data');
    data.setUint32(40, samples.length * 2, Endian.little);
    for (var i = 0; i < samples.length; i++) {
      data.setInt16(44 + i * 2, (samples[i].clamp(-1.0, 1.0) * 32767).round(), Endian.little);
    }
    return data.buffer.asUint8List();
  }
}
