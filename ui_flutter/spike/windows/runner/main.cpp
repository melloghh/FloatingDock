#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include "flutter_window.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize COM, so that it is available for use in the library and/or
  // plugins.
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  flutter::DartProject project(L"data");

  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project);
  // Panel 360x56 plus 28 DIP of transparent margin on every side; top centre of a 1920-wide screen.
  Win32Window::Point origin(780, 0);
  Win32Window::Size size(416, 112);
  if (!window.Create(L"fd_spike", origin, size)) {
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  // Spike: FD_EXTRA=N opens N more views on the same engine 3 s after start.
  static int extra_count = 0;
  {
    wchar_t buffer[8] = {};
    GetEnvironmentVariableW(L"FD_EXTRA", buffer, 8);
    extra_count = _wtoi(buffer);
  }
  if (extra_count > 0) {
    ::SetTimer(nullptr, 1, 3000, [](HWND, UINT, UINT_PTR id, DWORD) {
      ::KillTimer(nullptr, id);
      for (int i = 0; i < extra_count; ++i) {
        auto* extra = new ExtraWindow();
        extra->Create(L"fd_extra", Win32Window::Point(780, 140 + i * 130), Win32Window::Size(416, 112));
      }
    });
  }

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  ::CoUninitialize();
  return EXIT_SUCCESS;
}
