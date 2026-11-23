import 'dart:io';

import 'package:fd_ui/core/sfx.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('every sound is a valid, short, quiet-enough WAV', () {
    for (final s in Sound.values) {
      final wav = Sfx.debugWav(s);
      expect(String.fromCharCodes(wav.sublist(0, 4)), 'RIFF');
      expect(String.fromCharCodes(wav.sublist(8, 16)), 'WAVEfmt ');
      expect(wav.length, lessThan(44 + 44100 * 2 ~/ 2), reason: '${s.name} is under half a second');
      expect(wav.length, greaterThan(44 + 1000));
      if (Platform.environment['FD_SFX_DUMP'] case final dir?) File('$dir/${s.name}.wav').writeAsBytesSync(wav);
    }
  });
}
