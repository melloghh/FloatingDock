#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

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
// Spike only: the C++ wrapper hides the raw controller, which is needed to reach the shared engine.
#define private public
#include <flutter/flutter_view_controller.h>
#undef private

#include "win32_window.h"

// A window that does nothing but host a Flutter view.
class FlutterWindow : public Win32Window {
 public:
  // Creates a new FlutterWindow hosting a Flutter view running |project|.
  explicit FlutterWindow(const flutter::DartProject& project);
  virtual ~FlutterWindow();

 protected:
  // Win32Window:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  // The project to run.
  flutter::DartProject project_;

  // The Flutter instance hosted by this window.
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;
};

// Engine shared by every window of the process (set by the first FlutterWindow).
extern FlutterDesktopEngineRef g_shared_engine;

// A further view on the shared engine: same process, same isolate, its own HWND.
class ExtraWindow : public Win32Window {
 protected:
  bool OnCreate() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  FlutterDesktopViewControllerRef controller_ = nullptr;
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
