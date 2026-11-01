#include "host.h"

#include <dwmapi.h>
#include <shellapi.h>
#include <shobjidl.h>

#include <cmath>

#include "flutter/generated_plugin_registrant.h"
#include "utils.h"

namespace {

constexpr wchar_t kWindowClass[] = L"FloatingDock.UiWindow";
constexpr UINT kAppBarMessage = WM_APP + 1;
bool g_class_registered = false;

using flutter::EncodableList;
using flutter::EncodableMap;
using flutter::EncodableValue;

std::wstring Wide(const std::string& s) {
  if (s.empty()) return L"";
  int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
  std::wstring out(n, L'\0');
  MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), n);
  return out;
}

const EncodableValue* Get(const EncodableMap& m, const char* key) {
  auto it = m.find(EncodableValue(std::string(key)));
  return it == m.end() ? nullptr : &it->second;
}

int64_t Int(const EncodableMap& m, const char* key, int64_t fallback = 0) {
  auto* v = Get(m, key);
  if (!v) return fallback;
  if (auto* i = std::get_if<int32_t>(v)) return *i;
  if (auto* l = std::get_if<int64_t>(v)) return *l;
  if (auto* d = std::get_if<double>(v)) return static_cast<int64_t>(std::lround(*d));
  return fallback;
}

double Dbl(const EncodableMap& m, const char* key, double fallback = 0) {
  auto* v = Get(m, key);
  if (!v) return fallback;
  if (auto* d = std::get_if<double>(v)) return *d;
  if (auto* i = std::get_if<int32_t>(v)) return *i;
  if (auto* l = std::get_if<int64_t>(v)) return static_cast<double>(*l);
  return fallback;
}

bool Bool(const EncodableMap& m, const char* key, bool fallback = false) {
  auto* v = Get(m, key);
  if (!v) return fallback;
  if (auto* b = std::get_if<bool>(v)) return *b;
  return fallback;
}

std::string Str(const EncodableMap& m, const char* key) {
  auto* v = Get(m, key);
  if (!v) return "";
  if (auto* s = std::get_if<std::string>(v)) return *s;
  return "";
}

UINT DpiAt(POINT pt) {
  return FlutterDesktopGetDpiForMonitor(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
}

// Per-pixel transparency: the glass frame covers the whole client area, so whatever Flutter paints with alpha shows the
// desktop through it. No window region and no blur are involved.
void MakeTransparent(HWND hwnd) {
  MARGINS margins = {-1, -1, -1, -1};
  DwmExtendFrameIntoClientArea(hwnd, &margins);
}

void ForceForeground(HWND hwnd) {
  HWND fg = GetForegroundWindow();
  DWORD fg_thread = fg ? GetWindowThreadProcessId(fg, nullptr) : 0;
  DWORD me = GetCurrentThreadId();
  if (fg_thread && fg_thread != me) AttachThreadInput(fg_thread, me, TRUE);
  SetForegroundWindow(hwnd);
  BringWindowToTop(hwnd);
  if (fg_thread && fg_thread != me) AttachThreadInput(fg_thread, me, FALSE);
}

}  // namespace

WindowHost::WindowHost(const flutter::DartProject& project) : project_(project) {}

WindowHost::~WindowHost() = default;

void WindowHost::RegisterWindowClass() {
  if (g_class_registered) return;
  WNDCLASS wc = {};
  wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
  wc.lpszClassName = kWindowClass;
  wc.hInstance = GetModuleHandle(nullptr);
  wc.hIcon = LoadIcon(wc.hInstance, MAKEINTRESOURCE(101));
  wc.hbrBackground = nullptr;
  wc.lpfnWndProc = WindowHost::WndProc;
  RegisterClass(&wc);
  g_class_registered = true;
}

bool WindowHost::Start() {
  RegisterWindowClass();
  // The hub is a 1x1 window that is never shown: view 0 must exist for the engine to start, and it carries no UI.
  hub_.hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kWindowClass, L"FloatingDock UI hub", WS_POPUP, -32000,
                             -32000, 1, 1, nullptr, nullptr, GetModuleHandle(nullptr), nullptr);
  if (!hub_.hwnd) return false;
  hub_controller_ = std::make_unique<flutter::FlutterViewController>(1, 1, project_);
  if (!hub_controller_->engine() || !hub_controller_->view()) return false;
  engine_ = FlutterDesktopViewControllerGetEngine(hub_controller_->controller_);
  RegisterPlugins(hub_controller_->engine());
  hub_.controller = hub_controller_->controller_;
  hub_.view_id = static_cast<int64_t>(hub_controller_->view_id());
  hub_.child = hub_controller_->view()->GetNativeWindow();
  SetParent(hub_.child, hub_.hwnd);
  windows_[hub_.hwnd] = nullptr;  // placeholder so the hub's messages are recognised; it has no HostWindow record

  channel_ = std::make_unique<flutter::MethodChannel<Value>>(hub_controller_->engine()->messenger(), "fd/windows",
                                                              &flutter::StandardMethodCodec::GetInstance());
  channel_->SetMethodCallHandler(
      [this](const auto& call, auto result) { OnMethod(call, std::move(result)); });
  hub_controller_->ForceRedraw();
  return true;
}

LRESULT CALLBACK WindowHost::WndProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
  if (message == WM_NCCREATE) {
    auto* create = reinterpret_cast<CREATESTRUCT*>(lparam);
    SetWindowLongPtr(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    EnableNonClientDpiScaling(hwnd);
  }
  auto* w = reinterpret_cast<HostWindow*>(GetWindowLongPtr(hwnd, GWLP_USERDATA));
  if (w && w->controller) {
    LRESULT result = 0;
    if (FlutterDesktopViewControllerHandleTopLevelWindowProc(w->controller, hwnd, message, wparam, lparam, &result))
      return result;
  }
  extern WindowHost* g_host;
  return g_host ? g_host->HandleMessage(w, hwnd, message, wparam, lparam) : DefWindowProc(hwnd, message, wparam, lparam);
}

WindowHost* g_host = nullptr;

LRESULT WindowHost::HandleMessage(HostWindow* w, HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
  if (!w) return DefWindowProc(hwnd, message, wparam, lparam);
  switch (message) {
    case WM_SIZE:
      if (w->child) {
        RECT rc;
        GetClientRect(hwnd, &rc);
        MoveWindow(w->child, 0, 0, rc.right - rc.left, rc.bottom - rc.top, TRUE);
      }
      return 0;
    case WM_GETMINMAXINFO: {
      if (w->framed && w->min_w > 0) {
        auto* info = reinterpret_cast<MINMAXINFO*>(lparam);
        info->ptMinTrackSize.x = w->min_w;
        info->ptMinTrackSize.y = w->min_h;
      }
      return 0;
    }
    case WM_ACTIVATE: {
      bool active = LOWORD(wparam) != WA_INACTIVE;
      if (active && w->child) SetFocus(w->child);
      Emit("activated", w, {{Value("active"), Value(active)}});
      return 0;
    }
    case WM_MOUSEACTIVATE:
      return w->no_activate ? MA_NOACTIVATE : DefWindowProc(hwnd, message, wparam, lparam);
    case WM_DPICHANGED: {
      auto* rc = reinterpret_cast<RECT*>(lparam);
      if (w->framed)
        SetWindowPos(hwnd, nullptr, rc->left, rc->top, rc->right - rc->left, rc->bottom - rc->top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
      Emit("dpiChanged", w);
      return 0;
    }
    case WM_CLOSE:
      // A window is hidden, never destroyed: Dart decides what a close means.
      Emit("closeRequested", w);
      return 0;
    case WM_DROPFILES: {
      HDROP drop = reinterpret_cast<HDROP>(wparam);
      UINT count = DragQueryFile(drop, 0xFFFFFFFF, nullptr, 0);
      EncodableList paths;
      for (UINT i = 0; i < count; ++i) {
        wchar_t buffer[MAX_PATH * 2];
        UINT n = DragQueryFile(drop, i, buffer, MAX_PATH * 2);
        if (n > 0) paths.push_back(Value(Utf8FromUtf16(buffer)));
      }
      DragFinish(drop);
      Emit("filesDropped", w, {{Value("paths"), Value(std::move(paths))}});
      return 0;
    }
    case WM_ERASEBKGND:
      return 1;
    case kAppBarMessage:
      if (wparam == ABN_FULLSCREENAPP) Emit("appBarFullscreen", w, {{Value("on"), Value(lparam != 0)}});
      return 0;
  }
  return DefWindowProc(hwnd, message, wparam, lparam);
}

void WindowHost::ClearThumbnails(HostWindow* w) {
  for (HTHUMBNAIL t : w->thumbnails) DwmUnregisterThumbnail(t);
  w->thumbnails.clear();
}

// Live previews of other windows, drawn by DWM over this window's client area. Rectangles are logical pixels
// relative to the client area. Sizes of the sources stay theirs: the caller computes the rectangles from their aspect.
void WindowHost::SetThumbnails(HostWindow* w, const flutter::EncodableList& items) {
  ClearThumbnails(w);
  double scale = GetDpiForWindow(w->hwnd) / 96.0;
  for (const auto& item : items) {
    const auto* m = std::get_if<Map>(&item);
    if (!m) continue;
    HWND source = reinterpret_cast<HWND>(static_cast<intptr_t>(Int(*m, "hwnd")));
    if (!IsWindow(source)) continue;
    HTHUMBNAIL thumb = nullptr;
    if (FAILED(DwmRegisterThumbnail(w->hwnd, source, &thumb)) || !thumb) continue;
    DWM_THUMBNAIL_PROPERTIES props = {};
    props.dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY;
    props.rcDestination = {static_cast<LONG>(std::lround(Dbl(*m, "x") * scale)), static_cast<LONG>(std::lround(Dbl(*m, "y") * scale)),
                           static_cast<LONG>(std::lround((Dbl(*m, "x") + Dbl(*m, "w")) * scale)),
                           static_cast<LONG>(std::lround((Dbl(*m, "y") + Dbl(*m, "h")) * scale))};
    props.fVisible = TRUE;
    props.opacity = static_cast<BYTE>(std::lround(Dbl(*m, "opacity", 1.0) * 255));
    props.fSourceClientAreaOnly = TRUE;
    DwmUpdateThumbnailProperties(thumb, &props);
    w->thumbnails.push_back(thumb);
  }
}

void WindowHost::Emit(const char* type, HostWindow* w, Map extra) {
  if (!channel_ || !w) return;
  extra[Value("type")] = Value(std::string(type));
  extra[Value("viewId")] = Value(w->view_id);
  channel_->InvokeMethod("event", std::make_unique<Value>(std::move(extra)));
}

HostWindow* WindowHost::Find(const Map& args) {
  auto it = by_view_.find(Int(args, "viewId", -1));
  return it == by_view_.end() ? nullptr : it->second;
}

HostWindow* WindowHost::Create(const Map& args, std::string* error) {
  auto record = std::make_unique<HostWindow>();
  HostWindow* w = record.get();
  w->framed = Bool(args, "framed");
  w->no_activate = Bool(args, "noActivate");
  int x = static_cast<int>(Int(args, "x")), y = static_cast<int>(Int(args, "y"));
  UINT dpi = DpiAt({x, y});
  double scale = dpi / 96.0;
  int cw = static_cast<int>(std::lround(Dbl(args, "w", 400) * scale));
  int ch = static_cast<int>(std::lround(Dbl(args, "h", 300) * scale));
  w->min_w = static_cast<int>(std::lround(Dbl(args, "minW") * scale));
  w->min_h = static_cast<int>(std::lround(Dbl(args, "minH") * scale));

  DWORD style = w->framed ? WS_OVERLAPPEDWINDOW : WS_POPUP;
  DWORD ex = 0;
  if (Bool(args, "topmost")) ex |= WS_EX_TOPMOST;
  if (Bool(args, "toolWindow", !w->framed)) ex |= WS_EX_TOOLWINDOW;
  if (w->no_activate) ex |= WS_EX_NOACTIVATE;
  if (Bool(args, "acceptFiles")) ex |= WS_EX_ACCEPTFILES;
  RECT rc = {0, 0, cw, ch};
  if (w->framed) AdjustWindowRectExForDpi(&rc, style, FALSE, ex, dpi);

  w->hwnd = CreateWindowEx(ex, kWindowClass, Wide(Str(args, "title")).c_str(), style, x, y, rc.right - rc.left,
                           rc.bottom - rc.top, nullptr, nullptr, GetModuleHandle(nullptr), w);
  if (!w->hwnd) {
    *error = "CreateWindowEx failed";
    return nullptr;
  }
  if (w->framed) {
    BOOL dark = Bool(args, "darkCaption", true) ? TRUE : FALSE;
    DwmSetWindowAttribute(w->hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &dark, sizeof(dark));
  } else {
    MakeTransparent(w->hwnd);
  }
  RECT client;
  GetClientRect(w->hwnd, &client);
  w->controller = FlutterDesktopViewControllerCreate(client.right - client.left, client.bottom - client.top, engine_);
  if (!w->controller) {
    DestroyWindow(w->hwnd);
    *error = "FlutterDesktopViewControllerCreate failed";
    return nullptr;
  }
  w->view_id = static_cast<int64_t>(FlutterDesktopViewControllerGetViewId(w->controller));
  w->child = FlutterDesktopViewGetHWND(FlutterDesktopViewControllerGetView(w->controller));
  SetParent(w->child, w->hwnd);
  MoveWindow(w->child, 0, 0, client.right - client.left, client.bottom - client.top, TRUE);
  ShowWindow(w->child, SW_SHOW);
  by_view_[w->view_id] = w;
  windows_[w->hwnd] = std::move(record);
  return w;
}

void WindowHost::Show(HostWindow* w, bool activate) {
  if (w->app_bar || (GetWindowLong(w->hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST))
    SetWindowPos(w->hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
  if (activate && !w->no_activate) {
    ShowWindow(w->hwnd, SW_SHOW);
    ForceForeground(w->hwnd);
  } else {
    ShowWindow(w->hwnd, SW_SHOWNOACTIVATE);
  }
}

void WindowHost::SetBounds(HostWindow* w, int x, int y, double width_dip, double height_dip) {
  UINT dpi = DpiAt({x, y});
  double scale = dpi / 96.0;
  int cw = static_cast<int>(std::lround(width_dip * scale));
  int ch = static_cast<int>(std::lround(height_dip * scale));
  RECT rc = {0, 0, cw, ch};
  if (w->framed) AdjustWindowRectExForDpi(&rc, GetWindowLong(w->hwnd, GWL_STYLE), FALSE, GetWindowLong(w->hwnd, GWL_EXSTYLE), dpi);
  SetWindowPos(w->hwnd, nullptr, x, y, rc.right - rc.left, rc.bottom - rc.top, SWP_NOZORDER | SWP_NOACTIVATE);
}

WindowHost::Value WindowHost::Monitors() {
  EncodableList list;
  struct Ctx { EncodableList* list; } ctx{&list};
  EnumDisplayMonitors(
      nullptr, nullptr,
      [](HMONITOR mon, HDC, LPRECT, LPARAM param) -> BOOL {
        MONITORINFO mi = {sizeof(mi)};
        GetMonitorInfo(mon, &mi);
        EncodableMap m;
        m[Value("x")] = Value(static_cast<int32_t>(mi.rcMonitor.left));
        m[Value("y")] = Value(static_cast<int32_t>(mi.rcMonitor.top));
        m[Value("w")] = Value(static_cast<int32_t>(mi.rcMonitor.right - mi.rcMonitor.left));
        m[Value("h")] = Value(static_cast<int32_t>(mi.rcMonitor.bottom - mi.rcMonitor.top));
        m[Value("wx")] = Value(static_cast<int32_t>(mi.rcWork.left));
        m[Value("wy")] = Value(static_cast<int32_t>(mi.rcWork.top));
        m[Value("ww")] = Value(static_cast<int32_t>(mi.rcWork.right - mi.rcWork.left));
        m[Value("wh")] = Value(static_cast<int32_t>(mi.rcWork.bottom - mi.rcWork.top));
        m[Value("dpi")] = Value(static_cast<int32_t>(FlutterDesktopGetDpiForMonitor(mon)));
        m[Value("primary")] = Value((mi.dwFlags & MONITORINFOF_PRIMARY) != 0);
        reinterpret_cast<Ctx*>(param)->list->push_back(Value(std::move(m)));
        return TRUE;
      },
      reinterpret_cast<LPARAM>(&ctx));
  return Value(std::move(list));
}

std::optional<std::string> WindowHost::PickFile(HostWindow* owner, const Map& args) {
  bool save = Bool(args, "save");
  IFileDialog* dialog = nullptr;
  HRESULT hr = CoCreateInstance(save ? CLSID_FileSaveDialog : CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&dialog));
  if (FAILED(hr)) return std::nullopt;
  std::vector<std::wstring> names, specs;
  std::vector<COMDLG_FILTERSPEC> filters;
  if (auto* v = Get(args, "filters")) {
    if (auto* list = std::get_if<EncodableList>(v)) {
      for (auto& item : *list) {
        if (auto* pair = std::get_if<EncodableList>(&item); pair && pair->size() == 2) {
          names.push_back(Wide(std::get<std::string>((*pair)[0])));
          specs.push_back(Wide(std::get<std::string>((*pair)[1])));
        }
      }
    }
  }
  for (size_t i = 0; i < names.size(); ++i) filters.push_back({names[i].c_str(), specs[i].c_str()});
  if (!filters.empty()) dialog->SetFileTypes(static_cast<UINT>(filters.size()), filters.data());
  auto name = Str(args, "fileName");
  if (!name.empty()) dialog->SetFileName(Wide(name).c_str());
  std::optional<std::string> path;
  if (SUCCEEDED(dialog->Show(owner ? owner->hwnd : nullptr))) {
    IShellItem* item = nullptr;
    if (SUCCEEDED(dialog->GetResult(&item))) {
      PWSTR raw = nullptr;
      if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &raw))) {
        path = Utf8FromUtf16(raw);
        CoTaskMemFree(raw);
      }
      item->Release();
    }
  }
  dialog->Release();
  return path;
}

// Reserves a strip of the screen on one edge (windows maximize beside it, like beside the taskbar).
WindowHost::Value WindowHost::SetAppBar(HostWindow* w, const Map& args) {
  UINT edge = static_cast<UINT>(Int(args, "edge", ABE_TOP));  // 0 left, 1 top, 2 right, 3 bottom
  int thickness = static_cast<int>(Int(args, "size"));
  RECT mon = {static_cast<LONG>(Int(args, "mx")), static_cast<LONG>(Int(args, "my")),
              static_cast<LONG>(Int(args, "mx") + Int(args, "mw")), static_cast<LONG>(Int(args, "my") + Int(args, "mh"))};
  APPBARDATA abd = {sizeof(abd)};
  abd.hWnd = w->hwnd;
  abd.uCallbackMessage = kAppBarMessage;
  if (!w->app_bar) {
    SHAppBarMessage(ABM_NEW, &abd);
    w->app_bar = true;
  }
  abd.uEdge = edge;
  abd.rc = mon;
  switch (edge) {
    case ABE_TOP: abd.rc.bottom = abd.rc.top + thickness; break;
    case ABE_BOTTOM: abd.rc.top = abd.rc.bottom - thickness; break;
    case ABE_LEFT: abd.rc.right = abd.rc.left + thickness; break;
    case ABE_RIGHT: abd.rc.left = abd.rc.right - thickness; break;
  }
  SHAppBarMessage(ABM_QUERYPOS, &abd);
  switch (edge) {
    case ABE_TOP: abd.rc.bottom = abd.rc.top + thickness; break;
    case ABE_BOTTOM: abd.rc.top = abd.rc.bottom - thickness; break;
    case ABE_LEFT: abd.rc.right = abd.rc.left + thickness; break;
    case ABE_RIGHT: abd.rc.left = abd.rc.right - thickness; break;
  }
  SHAppBarMessage(ABM_SETPOS, &abd);
  w->app_bar_edge = edge;
  EncodableMap out;
  out[Value("x")] = Value(static_cast<int32_t>(abd.rc.left));
  out[Value("y")] = Value(static_cast<int32_t>(abd.rc.top));
  out[Value("w")] = Value(static_cast<int32_t>(abd.rc.right - abd.rc.left));
  out[Value("h")] = Value(static_cast<int32_t>(abd.rc.bottom - abd.rc.top));
  return Value(std::move(out));
}

void WindowHost::RemoveAppBar(HostWindow* w) {
  if (!w->app_bar) return;
  APPBARDATA abd = {sizeof(abd)};
  abd.hWnd = w->hwnd;
  SHAppBarMessage(ABM_REMOVE, &abd);
  w->app_bar = false;
}

void WindowHost::OnMethod(const flutter::MethodCall<Value>& call, std::unique_ptr<Result> result) {
  const auto& name = call.method_name();
  static const Map kEmpty;
  const Map* args = call.arguments() ? std::get_if<Map>(call.arguments()) : nullptr;
  const Map& a = args ? *args : kEmpty;

  if (name == "monitors") return result->Success(Monitors());
  if (name == "cursor") {
    POINT pt;
    GetCursorPos(&pt);
    Map m;
    m[Value("x")] = Value(static_cast<int32_t>(pt.x));
    m[Value("y")] = Value(static_cast<int32_t>(pt.y));
    return result->Success(Value(std::move(m)));
  }
  if (name == "create") {
    std::string error;
    HostWindow* w = Create(a, &error);
    if (!w) return result->Error("create-failed", error);
    Map m;
    m[Value("viewId")] = Value(w->view_id);
    m[Value("hwnd")] = Value(static_cast<int64_t>(reinterpret_cast<intptr_t>(w->hwnd)));
    return result->Success(Value(std::move(m)));
  }
  if (name == "exit") {
    PostQuitMessage(0);
    return result->Success();
  }
  HostWindow* w = Find(a);
  if (!w) return result->Error("no-such-view", "Unknown viewId");

  if (name == "show") {
    Show(w, Bool(a, "activate", true));
  } else if (name == "hide") {
    ClearThumbnails(w);
    ShowWindow(w->hwnd, SW_HIDE);
  } else if (name == "thumbnails") {
    if (auto* v = Get(a, "items")) {
      if (auto* list = std::get_if<EncodableList>(v)) SetThumbnails(w, *list); else ClearThumbnails(w);
    } else {
      ClearThumbnails(w);
    }
  } else if (name == "windowSize") {
    // Pixel size of another window (to lay out its preview with the right aspect).
    RECT r;
    HWND other = reinterpret_cast<HWND>(static_cast<intptr_t>(Int(a, "hwnd")));
    Map m;
    if (IsWindow(other) && GetClientRect(other, &r)) {
      m[Value("w")] = Value(static_cast<int32_t>(r.right - r.left));
      m[Value("h")] = Value(static_cast<int32_t>(r.bottom - r.top));
    }
    return result->Success(Value(std::move(m)));
  } else if (name == "setNoActivate") {
    w->no_activate = Bool(a, "on", true);
    LONG_PTR ex = GetWindowLongPtr(w->hwnd, GWL_EXSTYLE);
    SetWindowLongPtr(w->hwnd, GWL_EXSTYLE, w->no_activate ? (ex | WS_EX_NOACTIVATE) : (ex & ~static_cast<LONG_PTR>(WS_EX_NOACTIVATE)));
  } else if (name == "setBounds") {
    SetBounds(w, static_cast<int>(Int(a, "x")), static_cast<int>(Int(a, "y")), Dbl(a, "w"), Dbl(a, "h"));
  } else if (name == "getBounds") {
    RECT r;
    GetWindowRect(w->hwnd, &r);
    RECT c;
    GetClientRect(w->hwnd, &c);
    POINT origin = {0, 0};
    ClientToScreen(w->hwnd, &origin);
    Map m;
    m[Value("x")] = Value(static_cast<int32_t>(origin.x));
    m[Value("y")] = Value(static_cast<int32_t>(origin.y));
    m[Value("w")] = Value(static_cast<int32_t>(c.right - c.left));
    m[Value("h")] = Value(static_cast<int32_t>(c.bottom - c.top));
    m[Value("dpi")] = Value(static_cast<int32_t>(GetDpiForWindow(w->hwnd)));
    m[Value("visible")] = Value(IsWindowVisible(w->hwnd) != FALSE);
    m[Value("active")] = Value(GetForegroundWindow() == w->hwnd);
    return result->Success(Value(std::move(m)));
  } else if (name == "setTopmost") {
    SetWindowPos(w->hwnd, Bool(a, "on", true) ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
  } else if (name == "setTitle") {
    SetWindowText(w->hwnd, Wide(Str(a, "title")).c_str());
  } else if (name == "setDarkCaption") {
    BOOL dark = Bool(a, "on", true) ? TRUE : FALSE;
    DwmSetWindowAttribute(w->hwnd, 20, &dark, sizeof(dark));
  } else if (name == "focus") {
    ForceForeground(w->hwnd);
  } else if (name == "pickFile") {
    auto path = PickFile(w, a);
    return path ? result->Success(Value(*path)) : result->Success();
  } else if (name == "appBar") {
    return result->Success(SetAppBar(w, a));
  } else if (name == "removeAppBar") {
    RemoveAppBar(w);
  } else {
    return result->NotImplemented();
  }
  result->Success();
}
