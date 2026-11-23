#ifndef RUNNER_HOST_H_
#define RUNNER_HOST_H_

#include <algorithm>
#include <functional>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <variant>
#include <vector>

#include <flutter/dart_project.h>
// The C++ wrapper hides the raw view controller; the engine handle behind it is what lets the runner
// attach further views (windows) to the same engine. See ui_flutter/SPIKE.md.
#define private public
#include <flutter/flutter_view_controller.h>
#undef private
#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>

#include <windows.h>
#include <dwmapi.h>

// One native window that shows one Flutter view.
struct HostWindow {
  HWND hwnd = nullptr;
  HWND child = nullptr;
  FlutterDesktopViewControllerRef controller = nullptr;
  int64_t view_id = 0;
  bool framed = false;       // a normal window with a title bar (settings); everything else is a borderless overlay
  bool no_activate = false;
  int min_w = 0, min_h = 0;  // physical pixels, framed windows only
  bool app_bar = false;
  bool topmost = false;      // created as an always-on-top window (its style can be lowered for a while, e.g. under a screenshot overlay)
  UINT app_bar_edge = 0;
  std::vector<HTHUMBNAIL> thumbnails;  // DWM live previews drawn over this window
};

// Creates and manages every window of the UI process and exposes that to Dart over the "fd/windows" channel.
// Windows are never destroyed while the process runs (destroying a secondary view controller would take the shared
// engine down); Dart hides and reuses them.
class WindowHost {
 public:
  explicit WindowHost(const flutter::DartProject& project);
  ~WindowHost();

  // Creates the hidden hub window that owns the engine and starts Dart. False if the engine could not start.
  bool Start();

 private:
  using Value = flutter::EncodableValue;
  using Map = flutter::EncodableMap;
  using Result = flutter::MethodResult<Value>;

  static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam);
  LRESULT HandleMessage(HostWindow* w, HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam);
  static void RegisterWindowClass();

  void OnMethod(const flutter::MethodCall<Value>& call, std::unique_ptr<Result> result);
  HostWindow* Find(const Map& args);
  HostWindow* Create(const Map& args, std::string* error);
  void Show(HostWindow* w, bool activate, bool raise);
  void SetBounds(HostWindow* w, int x, int y, double width_dip, double height_dip);
  Value Monitors();
  std::optional<std::string> PickFile(HostWindow* owner, const Map& args);
  Value SetAppBar(HostWindow* w, const Map& args);
  void RemoveAppBar(HostWindow* w);
  void Emit(const char* type, HostWindow* w, Map extra = {});
  void SetThumbnails(HostWindow* w, const flutter::EncodableList& items);
  void ClearThumbnails(HostWindow* w);

  flutter::DartProject project_;
  std::unique_ptr<flutter::FlutterViewController> hub_controller_;
  HostWindow hub_;
  FlutterDesktopEngineRef engine_ = nullptr;
  std::unique_ptr<flutter::MethodChannel<Value>> channel_;
  std::map<HWND, std::unique_ptr<HostWindow>> windows_;
  std::map<int64_t, HostWindow*> by_view_;
};

#endif  // RUNNER_HOST_H_
