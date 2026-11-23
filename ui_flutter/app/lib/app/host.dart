import 'dart:async';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';

import '../core/app_state.dart';
import '../core/native.dart';

/// How a native window is created. Sizes are logical (DIP); the position is physical screen pixels.
class WindowSpec {
  const WindowSpec({
    required this.kind,
    required this.x,
    required this.y,
    required this.width,
    required this.height,
    this.framed = false,
    this.noActivate = false,
    this.topmost = false,
    this.toolWindow = true,
    this.title = '',
    this.minWidth = 0,
    this.minHeight = 0,
    this.darkCaption = true,
    this.acceptFiles = false,
  });

  final String kind;
  final int x, y;
  final double width, height, minWidth, minHeight;
  final bool framed, noActivate, topmost, toolWindow, darkCaption, acceptFiles;
  final String title;
}

/// One native window with one Flutter view. Windows are created once and then shown and hidden (never destroyed).
class WindowHandle {
  WindowHandle._(this.viewId, this.hwnd, this.kind, this.topmost);

  final int viewId;
  final int hwnd;
  final String kind;

  /// Whether the window is created above ordinary windows (bar, dock, popups).
  final bool topmost;

  /// True while the window is on screen. Widgets use it to start their pop animation.
  final visible = ValueNotifier<bool>(false);

  Future<void> show({bool activate = true}) async {
    visible.value = true;
    await native.show(viewId, activate: activate, raise: !WindowHost.underOverlay);
  }

  Future<void> hide() async {
    visible.value = false;
    await native.hide(viewId);
  }

  Future<void> setBounds({required int x, required int y, required double width, required double height}) =>
      native.setBounds(viewId, x: x, y: y, w: width, h: height);

  Future<Map<String, Object?>> bounds() => native.getBounds(viewId);
}

/// Creates windows and keeps the list of what each Flutter view shows.
class WindowHost extends ChangeNotifier {
  final _builders = <int, WidgetBuilder>{};
  final _handles = <int, WindowHandle>{};

  Iterable<WindowHandle> get windows => _handles.values;
  WidgetBuilder? builderFor(int viewId) => _builders[viewId];
  WindowHandle? handleFor(int viewId) => _handles[viewId];

  Future<WindowHandle> create(WindowSpec spec, WidgetBuilder builder) async {
    final created = await native.create(
      kind: spec.kind,
      x: spec.x,
      y: spec.y,
      w: spec.width,
      h: spec.height,
      framed: spec.framed,
      noActivate: spec.noActivate,
      topmost: spec.topmost,
      toolWindow: spec.toolWindow,
      title: spec.title,
      minW: spec.minWidth,
      minH: spec.minHeight,
      darkCaption: spec.darkCaption,
      acceptFiles: spec.acceptFiles,
    );
    final handle = WindowHandle._(created.viewId, created.hwnd, spec.kind, spec.topmost);
    _handles[created.viewId] = handle;
    _builders[created.viewId] = builder;
    notifyListeners();
    return handle;
  }

  /// True while a screenshot selection overlay is up. The engine puts our always-on-top windows under it (and back); this only
  /// keeps windows that are shown meanwhile (the dock sliding in) from raising themselves above it.
  static bool underOverlay = false;
}

/// The root of the whole process: one [View] per native window, each wrapped in the shared state and theme.
class Root extends StatefulWidget {
  const Root({super.key, required this.state, required this.host});
  final AppState state;
  final WindowHost host;

  @override
  State<Root> createState() => _RootState();
}

class _RootState extends State<Root> with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    widget.host.addListener(_changed);
  }

  @override
  void dispose() {
    widget.host.removeListener(_changed);
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  void _changed() => setState(() {});

  // A native window that is added or removed shows up as a new view here.
  @override
  void didChangeMetrics() => setState(() {});

  @override
  void didChangePlatformBrightness() => widget.state.systemBrightnessChanged();

  @override
  Widget build(BuildContext context) {
    final views = <Widget>[];
    for (final view in ui.PlatformDispatcher.instance.views) {
      final builder = widget.host.builderFor(view.viewId);
      if (builder == null) continue;
      views.add(View(view: view, child: ViewShell(state: widget.state, builder: builder)));
    }
    return ViewCollection(views: views);
  }
}

/// Everything a window needs around its content: state, theme, localisation, a navigator for dialogs.
class ViewShell extends StatelessWidget {
  const ViewShell({super.key, required this.state, required this.builder});
  final AppState state;
  final WidgetBuilder builder;

  @override
  Widget build(BuildContext context) => AppScope(
        state: state,
        child: ListenableBuilder(
          listenable: state,
          builder: (context, _) => MaterialApp(
            debugShowCheckedModeBanner: false,
            theme: state.theme,
            scrollBehavior: const _NoGlowScroll(),
            home: Builder(builder: builder),
          ),
        ),
      );
}

class _NoGlowScroll extends MaterialScrollBehavior {
  const _NoGlowScroll();
  @override
  Widget buildOverscrollIndicator(BuildContext context, Widget child, ScrollableDetails details) => child;
}
