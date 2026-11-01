import 'dart:async';

import 'package:flutter/material.dart';

import '../core/app_state.dart';

/// Loads data from the engine and loads it again whenever one of [refreshOn] events arrives.
/// Shows the previous data while a reload is running, so lists never flash empty.
class LiveData<T> extends StatefulWidget {
  const LiveData({super.key, required this.load, required this.builder, this.refreshOn = const {}});

  final Future<T> Function(AppState state) load;
  final Set<String> refreshOn;
  final Widget Function(BuildContext context, T? data, AppState state) builder;

  @override
  State<LiveData<T>> createState() => _LiveDataState<T>();
}

class _LiveDataState<T> extends State<LiveData<T>> {
  T? _data;
  StreamSubscription<dynamic>? _sub;
  AppState? _state;
  bool _loading = false, _again = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final state = AppScope.read(context);
    if (!identical(state, _state)) {
      _sub?.cancel();
      _state = state;
      _sub = state.eventsOther.listen((e) {
        if (widget.refreshOn.contains(e.name)) _reload();
      });
      _reload();
    }
  }

  Future<void> _reload() async {
    if (_loading) {
      _again = true;
      return;
    }
    _loading = true;
    try {
      final data = await widget.load(_state!);
      if (mounted) setState(() => _data = data);
    } catch (_) {
      // Keep what is shown; the next event or connection tries again.
    } finally {
      _loading = false;
    }
    if (_again && mounted) {
      _again = false;
      await _reload();
    }
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.builder(context, _data, _state!);
}
