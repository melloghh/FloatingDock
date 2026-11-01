#include <flutter/dart_project.h>
#include <windows.h>

#include "host.h"
#include "utils.h"

extern WindowHost* g_host;

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev, _In_ wchar_t* command_line, _In_ int show_command) {
  // Attach to the console when started from one (`flutter run`, the engine's diagnostics); never create one.
  ::AttachConsole(ATTACH_PARENT_PROCESS);

  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  flutter::DartProject project(L"data");
  project.set_dart_entrypoint_arguments(GetCommandLineArguments());

  WindowHost host(project);
  g_host = &host;
  if (!host.Start()) return EXIT_FAILURE;

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }
  g_host = nullptr;
  ::CoUninitialize();
  return EXIT_SUCCESS;
}
