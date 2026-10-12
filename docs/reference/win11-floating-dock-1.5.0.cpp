// ==WindhawkMod==
// @id              win11-floating-dock
// @name            Floating Dock (macOS-style Taskbar)
// @description     Replaces the Windows 11 taskbar with a floating macOS-style Dock: Start button, pinned + running apps exactly like the taskbar, continuous magnification, icon packs, native context menus, drag-to-reorder, multi-monitor, DPI aware.
// @version         1.5.0
// @author          Mello
// @include         explorer.exe
// @architecture    x86-64
// @compilerOptions -ld2d1 -ldwrite -lwindowscodecs -lole32 -loleaut32 -lshell32 -lshlwapi -ldwmapi -lshcore -luuid -lversion -lgdi32 -luser32 -ladvapi32
// @license         MIT
// ==/WindhawkMod==

// ==WindhawkModReadme==
/*
# Floating Dock

Replaces the look and feel of the Windows 11 taskbar with a compact, floating, rounded Dock. The mod only runs inside the shell `explorer.exe`; it installs no hooks in other programs and has no window animations of its own.

* **Start button** (optional, setting *Show the Windows Start button*): the first item. It is not an app - it is not pinned, not saved in the order and cannot be dragged. Click = the real Start menu (click again = closes it), right click = the Win+X menu.
* **Pinned apps** come from Windows itself: the shortcuts in `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar` and their real order, parsed structurally from `HKCU\...\Explorer\Taskband\Favorites`. Pins that have no shortcut file (AppID only) are picked up too; the same application listed twice (a shortcut and an AppID entry, for instance) is shown once.
* **Running apps behave like on the Windows taskbar**: every normal top-level window (visible, not a tool window, not owned by another window, not cloaked) shows up as its app's icon - at the pin's place when the app is pinned, otherwise at the end of the row - and disappears when the last window is closed. One app is always one icon: windows of the same program are merged even if only some of them report an AppUserModelID (File Explorer), and a pin without a target of its own (File Explorer pinned by AppID) is matched through its AppID. There is no separate "running apps" section. Windows that cannot be attributed to an app (yet) are shown as an item of their own, never dropped. Store / UWP apps (Settings...) are matched by AppUserModelID; a window is looked at again a few times while it is not ready, and the whole list is re-checked every 3 s as a safety net.
* **Icons appear and disappear with an animation**: an icon of an app that opens grows and fades in, one whose app closed (or that was unpinned) shrinks and fades out while the others close the gap; for a pinned app only the running indicator fades. Settings *Animate icons appearing...* and *Icon appear / disappear duration*.
* **Icons** are real shell icons (or PNG icon-pack files), loaded and decoded on a background thread, cached, and drawn with Direct2D at the exact DPI. Nothing is decoded or extracted while the Dock is animating.
* **Magnification** is continuous and has no lag: the row is warped by a smooth "fisheye" map `X(x) = x + A * integral(bell)` (closed form) around the cursor position of the current frame, so icon positions and sizes are smooth functions of the cursor and the icon under the cursor stays under it. Hovering is decided from the cursor position against a stable zone (the resting plate plus the room magnified icons need), not from window messages, so a glitchy `WM_MOUSELEAVE` cannot interrupt the zoom.
* **Frame pacing**: animation runs on real elapsed time, paced to the display's vertical blank by a small pacer thread; it sleeps completely when nothing moves.
* **Original taskbar**: hidden completely (clock, tray, date, notification icons included) by giving every `Shell_TrayWnd` / `Shell_SecondaryTrayWnd` an empty window region on its own thread; re-applied after Explorer restarts, display / DPI changes and when another program replaces it, and everything is restored when the mod is unloaded.
* **Names**, **click / middle click / wheel**, **native context menu + Recent items**, **drag to reorder / drag off to unpin**, **auto-hide**, **multi-monitor** work as on a taskbar.

MIT License. Copyright (c) Mello. Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files, to deal in the software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the software, and to permit persons to whom the software is furnished to do so, subject to the following conditions: the above copyright notice and this permission notice shall be included in all copies or substantial portions of the software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Icon packs
A Windhawk mod is a single source file and cannot ship PNG files, so the pack lives in a folder (setting *Icon pack folder*; empty = `%LOCALAPPDATA%\FloatingDock\icons`). Put the PNG files there; sub-folders up to two levels deep are scanned too (root first, then sub-folders alphabetically; the first file found for a name wins).

* **How a file finds its app.** The Dock knows several names of every app (exe name, AppUserModelID parts for Store apps, shortcut name, display name such as "Adobe Photoshop 2024") and looks for a file in this order, the first rule that finds a file wins:
  1. exact name: `photoshop.png` for `Photoshop.exe`; case, spaces, dots and dashes are ignored (`ms-teams.png` = `msteams`);
  2. name without noise words: vendor / edition words (adobe, microsoft, google, app, desktop, pro, cc, cs), versions and years are ignored on both sides, so `Adobe Photoshop 2024.png`, `Photoshop CC.png` and `photoshop.png` all belong to Photoshop;
  3. same pronunciation, also across alphabets: `Фотошоп.png` for Photoshop, `Дискорд.png` for Discord, `Телеграм.png` for Telegram;
  4. one name contains the other (at least 4 letters and at least half of the longer name): `photoshop-icon-flat.png`;
  5. a one-letter difference in a Cyrillic name (`Спотифай.png` for Spotify).
  Names that are too short or too generic are never matched loosely. A few well-known real-name aliases (Edge = msedge, Calculator = calculatorapp...) are built in. If an icon is not picked up, turn on *Diagnostics*: the Windhawk log then shows, for every app, which file was used (and by which rule) or which names were tried - name the file after one of them.
* `name_dark.png` is preferred over `name.png` when *Prefer _dark variants* is on. `*_mask.png`, `config.ini`, etc. are ignored.
* The folder is watched: adding, replacing or removing PNG files is picked up within ~8 seconds without touching any setting (changing any setting also reloads the pack at once).

## Known limitations
Not verified on real hardware by the author (compiled and code-reviewed only). The Dock window is a layered window updated with `UpdateLayeredWindow` (CPU Direct2D), not a GPU swap chain. The Start button opens the Start menu by sending a Windows-key tap (there is no public API). *Diagnostics* writes a few rate-limited lines to the Windhawk log.
*/
// ==/WindhawkModReadme==

// ==WindhawkModSettings==
/*
- iconSize: 48
  $name: Icon size (DIP)
- maxScale: 170
  $name: Maximum icon scale (%)
- magnificationRadius: 150
  $name: Magnification radius (DIP; distance from the cursor over which icons are enlarged)
- iconSpacing: 6
  $name: Icon spacing (DIP)
- dockHeight: 64
  $name: Dock plate height (DIP; padding around the icons = (height - icon size) / 2)
- bottomOffset: 10
  $name: Offset from the bottom screen edge (DIP)
- widthMode: auto
  $name: Width mode
  $options:
  - auto: Fit to icons
  - fixed: Fixed width
- fixedWidth: 900
  $name: Fixed width (DIP)
- alignment: center
  $name: Alignment
  $options:
  - center: Center
  - left: Left
  - right: Right
- opacity: 55
  $name: Background opacity (0-100)
- backgroundColor: "1E1E1E"
  $name: Background colour (RRGGBB)
- cornerRadius: 22
  $name: Corner radius (DIP)
- shadow: true
  $name: Shadow
- border: true
  $name: Thin border highlight
- blur: false
  $name: Blur / acrylic backdrop (undocumented DWM accent API, best effort)
- activeIndicator: true
  $name: Show active indicator
- runningIndicator: true
  $name: Show indicator for running apps
- indicatorStyle: dot
  $name: Indicator style
  $options:
  - dot: Dot
  - bar: Bar
  - glow: Glow
- magnification: true
  $name: Enable magnification
- magnificationStrength: 100
  $name: Magnification strength (0-100 %, how much of the maximum scale is applied)
- animationSmoothness: 1
  $name: Optional extra smoothing of the zoom position (1-10; 1 = off = the zoom follows the cursor with no delay)
- animationSpeed: 6
  $name: Animation speed (1-10, zoom fade in/out, hide/show, drag slide, name label)
- iconAnimation: true
  $name: Animate icons appearing (app opens) and disappearing (app closes); the running indicator of pinned apps fades
- iconAnimationDuration: 240
  $name: Icon appear / disappear duration (ms, 80-800)
- showLabels: true
  $name: Show app names above the hovered icon
- nameLabelDelay: 0
  $name: Name label delay (ms; 0 = the name appears at once)
- useIconPack: true
  $name: Use PNG icon pack
- iconPackFolder: ""
  $name: Icon pack folder (empty = %LOCALAPPDATA%\FloatingDock\icons; sub-folders are scanned)
- iconPackPreferDark: true
  $name: Prefer _dark variants of pack icons
- taskbarMode: hideAll
  $name: Original Windows taskbar
  $options:
  - hideAll: Hide completely (clock, tray and notification icons too)
  - none: Leave visible (diagnostic only)
- autoHideWindowsTaskbar: true
  $name: Auto-hide the Windows taskbar (frees the bottom of the screen; restored when the mod is unloaded)
- reserveWorkArea: true
  $name: Maximised windows stop above the Dock (needs the option above; ignored when the Dock auto-hides)
- showPinned: true
  $name: Show pinned apps
- showStartButton: true
  $name: Show the Windows Start button (the first item; turn off to remove it)
- minimizeOnClick: true
  $name: Clicking the active app minimises it
- autoHide: false
  $name: Auto-hide the Dock
- showOnHover: true
  $name: Reveal by touching the bottom screen edge
- monitorMode: primary
  $name: Multi-monitor mode (docks)
  $options:
  - primary: Primary monitor only
  - taskbars: Monitors that have a taskbar
  - all: Every monitor
- debugLog: false
  $name: Diagnostics - write hover / running-window decisions to the Windhawk log (rate limited; for bug reports)
*/
// ==/WindhawkModSettings==

#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <propsys.h>
#include <dwmapi.h>
#include <shellscalingapi.h>
#include <d2d1.h>
#include <d2d1helper.h>
#include <dwrite.h>
#include <wincodec.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <deque>
#include <cwchar>
#include <cwctype>
#include <cstdint>
#include <memory>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifndef WS_EX_NOREDIRECTIONBITMAP
#define WS_EX_NOREDIRECTIONBITMAP 0x00200000L
#endif

// ============================================================================
// 0. Small utilities
// ============================================================================
namespace {

// Минимальный мьютекс на SRWLOCK (без зависимости от libc++ threading).
struct SrwMutex {
    SRWLOCK l = SRWLOCK_INIT;
    SrwMutex() = default;
    SrwMutex(const SrwMutex&) = delete;
    SrwMutex& operator=(const SrwMutex&) = delete;
    void lock() { AcquireSRWLockExclusive(&l); }
    void unlock() { ReleaseSRWLockExclusive(&l); }
};
struct SrwGuard {
    SrwMutex& m;
    explicit SrwGuard(SrwMutex& mm) : m(mm) { m.lock(); }
    ~SrwGuard() { m.unlock(); }
    SrwGuard(const SrwGuard&) = delete;
    SrwGuard& operator=(const SrwGuard&) = delete;
};

template <class T>
class Com {
    T* p_ = nullptr;
public:
    Com() = default;
    Com(const Com& o) : p_(o.p_) { if (p_) p_->AddRef(); }
    Com(Com&& o) noexcept : p_(o.p_) { o.p_ = nullptr; }
    Com& operator=(const Com& o) { if (this != &o) { reset(); p_ = o.p_; if (p_) p_->AddRef(); } return *this; }
    Com& operator=(Com&& o) noexcept { if (this != &o) { reset(); p_ = o.p_; o.p_ = nullptr; } return *this; }
    ~Com() { reset(); }
    void reset() { if (p_) { p_->Release(); p_ = nullptr; } }
    T** put() { reset(); return &p_; }
    T* get() const { return p_; }
    T* operator->() const { return p_; }
    explicit operator bool() const { return p_ != nullptr; }
};

double NowSec() {
    static const double freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return (double)f.QuadPart; }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / freq;
}

std::wstring Lower(std::wstring s) {
    for (auto& c : s) c = (wchar_t)towlower(c);
    return s;
}
bool IEq(const std::wstring& a, const std::wstring& b) {
    return a.size() == b.size() && _wcsicmp(a.c_str(), b.c_str()) == 0;
}
std::wstring Expand(const std::wstring& s) {
    if (s.empty()) return s;
    DWORD n = ExpandEnvironmentStringsW(s.c_str(), nullptr, 0);
    if (!n) return s;
    std::wstring r(n, L'\0');
    n = ExpandEnvironmentStringsW(s.c_str(), &r[0], n);
    if (!n) return s;
    r.resize(n - 1);
    return r;
}
// Canonical form of an executable path for comparisons: environment variables expanded, forward slashes, no \\?\ prefix, lower case.
std::wstring NormPath(const std::wstring& p) {
    if (p.empty()) return p;
    static std::unordered_map<std::wstring, std::wstring> cache;
    auto f = cache.find(p);
    if (f != cache.end()) return f->second;
    std::wstring r = Expand(p);
    for (auto& c : r) if (c == L'/') c = L'\\';
    if (r.compare(0, 4, L"\\\\?\\") == 0) r.erase(0, 4);
    WCHAR lp[MAX_PATH * 2];
    DWORD n = GetLongPathNameW(r.c_str(), lp, ARRAYSIZE(lp));  // 8.3 short names -> long names
    if (n && n < ARRAYSIZE(lp)) r.assign(lp, n);
    for (auto& c : r) c = (wchar_t)(ULONG_PTR)CharLowerW((LPWSTR)(ULONG_PTR)c);
    if (cache.size() > 600) cache.clear();
    cache[p] = r;
    return r;
}
std::wstring BaseNameNoExt(const std::wstring& p) {
    size_t s = p.find_last_of(L"\\/");
    std::wstring f = s == std::wstring::npos ? p : p.substr(s + 1);
    size_t d = f.find_last_of(L'.');
    return d == std::wstring::npos ? f : f.substr(0, d);
}
std::vector<std::wstring> Split(const std::wstring& s, wchar_t sep) {
    std::vector<std::wstring> r;
    size_t i = 0;
    while (i <= s.size()) {
        size_t j = s.find(sep, i);
        if (j == std::wstring::npos) j = s.size();
        if (j > i) r.push_back(s.substr(i, j - i));
        i = j + 1;
    }
    return r;
}
std::wstring GetValueStr(PCWSTR name) {
    std::wstring buf(16384, L'\0');
    size_t n = Wh_GetStringValue(name, &buf[0], buf.size());
    (void)n;
    buf.resize(wcsnlen(buf.c_str(), buf.size()));
    return buf;
}
std::vector<std::wstring> LoadList(PCWSTR name) { return Split(GetValueStr(name), L'\n'); }
void SaveList(PCWSTR name, const std::vector<std::wstring>& v) {
    std::wstring s;
    for (auto& e : v) { s += e; s += L'\n'; }
    if (s.size() > 15000) s.resize(15000);
    Wh_SetStringValue(name, s.c_str());
}

// ============================================================================
// 1. Settings
// ============================================================================
struct Settings {
    int iconSize = 48, iconSpacing = 8, dockHeight = 68, bottomOffset = 10, fixedWidth = 900;
    bool fixedMode = false;
    int alignment = 0;  // 0 center, 1 left, 2 right
    int opacity = 55;
    uint32_t bg = 0x1E1E1E;
    int cornerRadius = 24;
    bool shadow = true, border = true, blur = false, activeIndicator = true, runningIndicator = true;
    int indicatorStyle = 0;  // 0 dot 1 bar 2 glow
    bool mag = true;
    int maxScalePct = 170, radius = 150, animSpeed = 6, magStrength = 100, animSmooth = 1;
    bool iconAnim = true;
    int iconAnimMs = 240;
    bool showLabels = true;
    int labelDelay = 0;
    bool useIconPack = true, packDark = true;
    std::wstring iconPackFolder;
    int taskbarMode = 2;  // 0 none (diagnostic), 2 hideAll (default; a saved 'keepTray' is migrated to hideAll)
    bool autoHideWinTaskbar = true, reserveWorkArea = true;
    bool showPinned = true, minimizeOnClick = true, showStart = true, debugLog = false;
    bool autoHide = false, showOnHover = true;
    int monitorMode = 0;  // 0 primary 1 taskbars 2 all
};
Settings S;

int GetI(PCWSTR n, int lo, int hi) {
    int v = Wh_GetIntSetting(n);
    return std::clamp(v, lo, hi);
}
std::wstring GetS(PCWSTR n) {
    PCWSTR s = Wh_GetStringSetting(n);
    std::wstring r = s ? s : L"";
    Wh_FreeStringSetting(s);
    return r;
}

// Hook-visible state (read from the taskbar thread, written from the dock thread).
std::atomic<int> g_hideMode{2};
std::atomic<bool> g_hookActive{false};

void LoadSettings() {
    S.iconSize = GetI(L"iconSize", 24, 128);
    S.iconSpacing = GetI(L"iconSpacing", 0, 48);
    S.dockHeight = GetI(L"dockHeight", 32, 200);
    S.bottomOffset = GetI(L"bottomOffset", 0, 200);
    S.fixedWidth = GetI(L"fixedWidth", 200, 8000);
    S.fixedMode = GetS(L"widthMode") == L"fixed";
    std::wstring a = GetS(L"alignment");
    S.alignment = a == L"left" ? 1 : a == L"right" ? 2 : 0;
    S.opacity = GetI(L"opacity", 0, 100);
    std::wstring c = GetS(L"backgroundColor");
    S.bg = (uint32_t)wcstoul(c.c_str(), nullptr, 16) & 0xFFFFFF;
    S.cornerRadius = GetI(L"cornerRadius", 0, 80);
    S.shadow = Wh_GetIntSetting(L"shadow") != 0;
    S.border = Wh_GetIntSetting(L"border") != 0;
    S.blur = Wh_GetIntSetting(L"blur") != 0;
    S.activeIndicator = Wh_GetIntSetting(L"activeIndicator") != 0;
    S.runningIndicator = Wh_GetIntSetting(L"runningIndicator") != 0;
    std::wstring st = GetS(L"indicatorStyle");
    S.indicatorStyle = st == L"bar" ? 1 : st == L"glow" ? 2 : 0;
    S.mag = Wh_GetIntSetting(L"magnification") != 0;
    S.maxScalePct = GetI(L"maxScale", 100, 300);
    S.radius = GetI(L"magnificationRadius", 40, 600);
    S.animSpeed = GetI(L"animationSpeed", 1, 10);
    S.magStrength = GetI(L"magnificationStrength", 0, 100);
    S.animSmooth = GetI(L"animationSmoothness", 1, 10);
    S.iconAnim = Wh_GetIntSetting(L"iconAnimation") != 0;
    S.iconAnimMs = GetI(L"iconAnimationDuration", 80, 800);
    S.showLabels = Wh_GetIntSetting(L"showLabels") != 0;
    S.labelDelay = GetI(L"nameLabelDelay", 0, 3000);
    S.useIconPack = Wh_GetIntSetting(L"useIconPack") != 0;
    S.packDark = Wh_GetIntSetting(L"iconPackPreferDark") != 0;
    S.iconPackFolder = GetS(L"iconPackFolder");
    std::wstring tm = GetS(L"taskbarMode");
    S.taskbarMode = tm == L"none" ? 0 : 2;  // 'hideAll', a previously saved 'keepTray' and anything unknown -> hide completely
    S.autoHideWinTaskbar = Wh_GetIntSetting(L"autoHideWindowsTaskbar") != 0;
    S.reserveWorkArea = Wh_GetIntSetting(L"reserveWorkArea") != 0;
    S.showPinned = Wh_GetIntSetting(L"showPinned") != 0;
    S.showStart = Wh_GetIntSetting(L"showStartButton") != 0;
    S.debugLog = Wh_GetIntSetting(L"debugLog") != 0;
    S.minimizeOnClick = Wh_GetIntSetting(L"minimizeOnClick") != 0;
    S.autoHide = Wh_GetIntSetting(L"autoHide") != 0;
    S.showOnHover = Wh_GetIntSetting(L"showOnHover") != 0;
    std::wstring mm = GetS(L"monitorMode");
    S.monitorMode = mm == L"all" ? 2 : mm == L"taskbars" ? 1 : 0;

    g_hideMode = S.taskbarMode;
}

// ============================================================================
// 2. Original taskbar hiding (window-region clipping) + SetWindowRgn hook
//    Public APIs only: SetWindowRgn / GetWindowRgn on Shell_TrayWnd and
//    Shell_SecondaryTrayWnd.
//
//    * The region is always changed ON THE TASKBAR'S OWN THREAD: the dock thread posts a
//      registered message to the taskbar window and a thread-local WH_GETMESSAGE hook (our
//      own code, same process) executes it. No cross-thread SetWindowRgn, no blocking.
//    * The only API hook is user32!SetWindowRgn, so Explorer cannot silently undo the clip.
//      Region ownership: a successful SetWindowRgn transfers the HRGN to the system, so a
//      region that Explorer passes in and that we replace is deleted by us only AFTER our own
//      region has been accepted; a copy is kept so it can be restored on unload.
// ============================================================================
typedef BOOL(WINAPI* SetWindowRgn_t)(HWND, HRGN, BOOL);
SetWindowRgn_t SetWindowRgn_Original = nullptr;

struct RgnSlot { std::atomic<HWND> hwnd{nullptr}; HRGN orig = nullptr; };
constexpr int kMaxTray = 8;
RgnSlot g_slots[kMaxTray];
CRITICAL_SECTION g_rgnCs;           // protects RgnSlot::orig and slot claiming
bool g_rgnCsInit = false;
UINT g_msgClip = 0;                 // registered message: wParam 1 = clip, 0 = restore, lParam = optional event
HINSTANCE g_hInst = nullptr;

BOOL CallSetWindowRgn(HWND h, HRGN r, BOOL redraw) {
    return SetWindowRgn_Original ? SetWindowRgn_Original(h, r, redraw) : SetWindowRgn(h, r, redraw);
}
RgnSlot* FindSlot(HWND h) {
    for (auto& s : g_slots) if (s.hwnd.load() == h) return &s;
    return nullptr;
}
// The taskbar is always hidden completely: an EMPTY window region clips the window and every child (clock, tray, date,
// language, notification icons, XAML islands), whatever its size / DPI / monitor is, so there is no geometry to get wrong.
HRGN BuildHideRegion(HWND) {
    return CreateRectRgn(0, 0, 0, 0);
}
HRGN CopyRegion(HRGN r) {
    if (!r) return nullptr;
    HRGN c = CreateRectRgn(0, 0, 0, 0);
    if (!c) return nullptr;
    if (CombineRgn(c, r, nullptr, RGN_COPY) == ERROR) { DeleteObject(c); return nullptr; }
    return c;
}

BOOL WINAPI SetWindowRgn_Hook(HWND hWnd, HRGN hRgn, BOOL bRedraw) {
    if (g_hookActive.load() && hWnd && g_rgnCsInit) {
        EnterCriticalSection(&g_rgnCs);
        RgnSlot* s = FindSlot(hWnd);
        if (s) {
            HRGN copy = CopyRegion(hRgn);  // remember what Explorer wanted
            if (s->orig) DeleteObject(s->orig);
            s->orig = copy;
            LeaveCriticalSection(&g_rgnCs);
            HRGN mine = BuildHideRegion(hWnd);
            if (mine && SetWindowRgn_Original(hWnd, mine, bRedraw)) {
                if (hRgn) DeleteObject(hRgn);  // system took 'mine'; Explorer's region is no longer used by anyone
                return TRUE;
            }
            if (mine) DeleteObject(mine);
            return SetWindowRgn_Original(hWnd, hRgn, bRedraw);  // could not clip: behave like the original call
        }
        LeaveCriticalSection(&g_rgnCs);
    }
    return SetWindowRgn_Original(hWnd, hRgn, bRedraw);
}

// --- executed on the taskbar window's thread ---------------------------------------
void ClipNow(HWND h) {
    if (!IsWindow(h) || !g_rgnCsInit) return;
    EnterCriticalSection(&g_rgnCs);
    RgnSlot* s = FindSlot(h);
    if (!s) {
        for (auto& c : g_slots) {
            if (!c.hwnd.load()) {
                HRGN o = CreateRectRgn(0, 0, 0, 0);
                if (o && GetWindowRgn(h, o) == ERROR) { DeleteObject(o); o = nullptr; }
                c.orig = o;
                c.hwnd.store(h);
                s = &c;
                break;
            }
        }
    }
    LeaveCriticalSection(&g_rgnCs);
    if (!s) return;
    HRGN mine = BuildHideRegion(h);
    if (mine && !CallSetWindowRgn(h, mine, TRUE)) DeleteObject(mine);
}
void UnclipNow(HWND h) {
    if (!g_rgnCsInit) return;
    EnterCriticalSection(&g_rgnCs);
    RgnSlot* s = FindSlot(h);
    if (!s) { LeaveCriticalSection(&g_rgnCs); return; }
    HRGN o = s->orig;
    s->orig = nullptr;
    s->hwnd.store(nullptr);  // from here on the hook ignores this window
    LeaveCriticalSection(&g_rgnCs);
    if (IsWindow(h)) {
        if (!CallSetWindowRgn(h, o, TRUE) && o) DeleteObject(o);
    } else if (o) {
        DeleteObject(o);
    }
}
LRESULT CALLBACK TaskbarGetMsgProc(int code, WPARAM w, LPARAM l) {
    if (code == HC_ACTION && w == PM_REMOVE && l) {
        MSG* m = (MSG*)l;
        if (g_msgClip && m->message == g_msgClip) {
            if (m->wParam == 1) { if (g_hookActive.load()) ClipNow(m->hwnd); }
            else UnclipNow(m->hwnd);
            if (m->lParam) SetEvent((HANDLE)m->lParam);
            m->message = WM_NULL;
        }
    }
    return CallNextHookEx(nullptr, code, w, l);
}

// --- dock-thread side -----------------------------------------------------------------
std::unordered_map<DWORD, HHOOK> g_tbHooks;   // taskbar thread id -> hook (dock thread only)
std::unordered_set<HWND> g_clipped;           // windows we asked to clip (dock thread only)

// Returns true when the operation was handed to the taskbar thread (or executed directly).
bool PostClipOp(HWND h, int op, HANDLE ev) {
    DWORD tid = GetWindowThreadProcessId(h, nullptr);
    if (!tid) { if (ev) SetEvent(ev); return false; }
    if (tid == GetCurrentThreadId()) {
        if (op) ClipNow(h); else UnclipNow(h);
        if (ev) SetEvent(ev);
        return true;
    }
    auto it = g_tbHooks.find(tid);
    if (it == g_tbHooks.end()) {
        HHOOK hk = SetWindowsHookExW(WH_GETMESSAGE, TaskbarGetMsgProc, g_hInst, tid);
        if (!hk) {  // last resort: direct call (never expected)
            if (op) ClipNow(h); else UnclipNow(h);
            if (ev) SetEvent(ev);
            return false;
        }
        it = g_tbHooks.emplace(tid, hk).first;
    }
    if (!PostMessageW(h, g_msgClip, (WPARAM)op, (LPARAM)ev)) { if (ev) SetEvent(ev); return false; }
    return true;
}
void ClipTaskbar(HWND h) {
    if (!IsWindow(h)) return;
    g_clipped.insert(h);
    PostClipOp(h, 1, nullptr);
}
void UnclipTaskbar(HWND h) {
    g_clipped.erase(h);
    PostClipOp(h, 0, nullptr);
}
// Synchronous restore used at unload: waits (bounded) until the taskbar thread has restored the regions.
void UnclipAllSync() {
    std::vector<HWND> wins(g_clipped.begin(), g_clipped.end());
    g_clipped.clear();
    std::vector<HANDLE> evs;
    for (HWND h : wins) {
        HANDLE e = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!e) continue;
        evs.push_back(e);
        PostClipOp(h, 0, e);
    }
    ULONGLONG t0 = GetTickCount64();
    for (HANDLE e : evs) {
        ULONGLONG el = GetTickCount64() - t0;
        DWORD wait = el >= 2500 ? 0 : (DWORD)(2500 - el);
        if (WaitForSingleObject(e, wait) == WAIT_TIMEOUT) Wh_Log(L"taskbar thread did not restore its region in time");
        CloseHandle(e);
    }
    // whatever is still registered (taskbar thread hung): restore directly as a last resort
    for (auto& s : g_slots) { HWND h = s.hwnd.load(); if (h) UnclipNow(h); }
    for (auto& kv : g_tbHooks) UnhookWindowsHookEx(kv.second);
    g_tbHooks.clear();
    Sleep(40);  // let a hook callback that may still be running on the taskbar thread leave our code
}

// Forgets slots of taskbar windows that no longer exist (their saved region is ours to free).
void PurgeDeadSlots() {
    if (!g_rgnCsInit) return;
    EnterCriticalSection(&g_rgnCs);
    for (auto& s : g_slots) {
        HWND h = s.hwnd.load();
        if (h && !IsWindow(h)) {
            if (s.orig) { DeleteObject(s.orig); s.orig = nullptr; }
            s.hwnd.store(nullptr);
        }
    }
    LeaveCriticalSection(&g_rgnCs);
}

void SetWinTaskbarAutoHide(bool on) {
    APPBARDATA abd{};
    abd.cbSize = sizeof(abd);
    abd.hWnd = FindWindowW(L"Shell_TrayWnd", nullptr);
    if (!abd.hWnd) return;
    UINT st = (UINT)SHAppBarMessage(ABM_GETSTATE, &abd);
    if (on) {
        if (!(st & ABS_AUTOHIDE)) {
            Wh_SetIntValue(L"restoreAutoHide", 1);  // crash recovery marker
            abd.lParam = (LPARAM)(st | ABS_AUTOHIDE);
            SHAppBarMessage(ABM_SETSTATE, &abd);
        }
    } else if (Wh_GetIntValue(L"restoreAutoHide", 0)) {
        abd.lParam = (LPARAM)(st & ~ABS_AUTOHIDE);
        SHAppBarMessage(ABM_SETSTATE, &abd);
        Wh_DeleteValue(L"restoreAutoHide");
    }
}

struct TrayEnum { std::vector<HWND> v; };
BOOL CALLBACK EnumTrayProc(HWND h, LPARAM lp) {
    WCHAR cls[64] = {};
    GetClassNameW(h, cls, 64);
    if (wcscmp(cls, L"Shell_TrayWnd") == 0 || wcscmp(cls, L"Shell_SecondaryTrayWnd") == 0) {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (pid == GetCurrentProcessId()) ((TrayEnum*)lp)->v.push_back(h);
    }
    return TRUE;
}
std::vector<HWND> FindOwnTaskbars() {
    TrayEnum te;
    EnumWindows(EnumTrayProc, (LPARAM)&te);
    return te.v;
}


// ============================================================================
// 3. Shared COM factories
// ============================================================================
Com<ID2D1Factory> g_d2d;
Com<IWICImagingFactory> g_wic;
Com<IDWriteFactory> g_dw;

bool InitFactories() {
    if (g_d2d && g_wic && g_dw) return true;
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, g_d2d.put()))) return false;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(g_wic.put())))) return false;
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)g_dw.put()))) return false;
    return true;
}

// ============================================================================
// 4. Model: pinned apps (from Windows), running windows, merged items
// ============================================================================
const PROPERTYKEY kPkeyAumid = {{0x9F4C2855, 0x9F79, 0x4B39, {0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3}}, 5};
// System.Link.TargetParsingPath
const PROPERTYKEY kPkeyLinkTarget = {{0xB9B4B3FC, 0x2B51, 0x4A42, {0xB5, 0xD8, 0x32, 0x41, 0x46, 0xAF, 0xCF, 0x25}}, 2};

struct Pin {
    std::wstring key, aumid, exe, lnk, name, iconPath;
    std::wstring args;   // lower-case shortcut arguments (a pin with arguments is not the plain app: Chrome / Edge web apps...)
    std::wstring mexe;   // exe used only for matching running windows (resolved from the AppUserModelID of a pin that has no target)
    int iconIdx = 0;
    bool extra = false;
};

struct DockItem {
    std::wstring key, name, aumid, exe, lnk, iconPath;
    int iconIdx = 0;
    bool pinned = false, extra = false, active = false;
    int special = 0;           // 0 = application, 1 = the Windows Start button (not an app: no pin / running model, no order, no drag)
    std::vector<HWND> wins;    // z-order, topmost first
    std::vector<HWND> stable;  // first-seen order (used for cycling)
    HWND cycleHwnd = nullptr;
    double bounceStart = 0;  // NowSec() of the launch bounce, 0 = none
};
using ItemPtr = std::shared_ptr<DockItem>;
const wchar_t kStartKey[] = L"\x03" L"start";  // key of the Start item (control character: can never collide with an app key)
std::vector<ItemPtr> g_items;
uint32_t g_itemsGen = 1;  // bumped whenever g_items was rebuilt (per-dock item state is re-synchronised by key)
std::vector<Pin> g_winPins;
std::unordered_map<std::wstring, size_t> g_seen;      // running-group key -> first-seen counter
size_t g_seenCounter = 0;
std::unordered_map<HWND, size_t> g_winSeq;            // window -> first-seen counter
size_t g_winSeqCounter = 0;
std::unordered_map<std::wstring, std::wstring> g_nameCache;

std::wstring GetAumidFromStore(IPropertyStore* ps) {
    std::wstring r;
    PROPVARIANT pv;
    PropVariantInit(&pv);
    if (SUCCEEDED(ps->GetValue(kPkeyAumid, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal) r = pv.pwszVal;
    PropVariantClear(&pv);
    return r;
}

// --- Pinned items: shortcut folder maintained by Explorer ------------------
std::wstring PinnedFolder() {
    WCHAR p[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_APPDATA, nullptr, 0, p))) return L"";
    return std::wstring(p) + L"\\Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar";
}

bool ReadFavoritesBlob(std::vector<BYTE>& out) {
    HKEY k;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Taskband", 0, KEY_READ, &k) != ERROR_SUCCESS) return false;
    DWORD sz = 0, type = 0;
    bool ok = false;
    if (RegQueryValueExW(k, L"Favorites", nullptr, &type, nullptr, &sz) == ERROR_SUCCESS && type == REG_BINARY && sz) {
        out.resize(sz);
        DWORD sz2 = sz;
        ok = RegQueryValueExW(k, L"Favorites", nullptr, &type, out.data(), &sz2) == ERROR_SUCCESS;
        if (ok) out.resize(sz2);
    }
    RegCloseKey(k);
    return ok;
}

// ---- Structural parser of Taskband\Favorites ------------------------------------------------
// The format is undocumented. Layout used here (observed, not guaranteed): a sequence of entries
//   [1 byte kind][uint32 pidlSize][PIDL of pidlSize bytes]   ... terminated by a single 0xFF byte.
// A PIDL is a list of SHITEMIDs ([uint16 cb][data], terminated by cb == 0). The last WORD of an item
// is the offset of its chain of extension blocks [uint16 size][uint16 version][uint32 signature][data].
//   0xBEEF001D: [uint16 type][AppID, UTF-16, NUL-terminated]       0xBEEF002C: retired entry
// Everything is bounds-checked; when the layout does not match, the caller falls back to a text search.
struct FavEntry {
    std::wstring appId;
    std::vector<std::wstring> names;  // printable UTF-16 strings found in the entry (file names, display names)
    bool retired = false;
};
inline uint16_t RdU16(const BYTE* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
inline uint32_t RdU32(const BYTE* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }

void CollectWide(const BYTE* p, size_t n, std::vector<std::wstring>& out) {
    for (size_t al = 0; al < 2; ++al) {
        size_t i = al;
        while (i + 1 < n) {
            size_t j = i;
            std::wstring s;
            while (j + 1 < n && s.size() < 520) {
                wchar_t c = (wchar_t)RdU16(p + j);
                if (c < 0x20 || c == 0x7F) break;
                s += c;
                j += 2;
            }
            bool terminated = j + 1 < n && RdU16(p + j) == 0;
            if (s.size() >= 3 && terminated) out.push_back(s);
            i = j + 2;
        }
    }
}

void ParseShellItem(const BYTE* it, size_t cb, FavEntry& fe) {
    if (cb < 6) return;
    size_t first = RdU16(it + cb - 2);
    std::vector<std::pair<size_t, size_t>> blocks;
    if (first >= 4 && first + 8 <= cb) {
        size_t pos = first;
        while (pos + 8 <= cb) {
            size_t sz = RdU16(it + pos);
            if (sz < 8 || pos + sz > cb || RdU16(it + pos + 6) != 0xBEEF) break;
            blocks.push_back({pos, sz});
            pos += sz;
        }
    }
    size_t headEnd = blocks.empty() ? cb : first;
    CollectWide(it + 2, headEnd > 2 ? headEnd - 2 : 0, fe.names);
    for (auto& b : blocks) {
        uint32_t sig = RdU32(it + b.first + 4);
        if (sig == 0xBEEF001D) {
            if (b.second >= 12) {
                std::wstring s;
                for (size_t q = b.first + 10; q + 1 < b.first + b.second; q += 2) {
                    wchar_t c = (wchar_t)RdU16(it + q);
                    if (!c) break;
                    s += c;
                }
                if (fe.appId.empty()) fe.appId = s;
            }
        } else if (sig == 0xBEEF002C) {
            fe.retired = true;
        } else if (b.second > 8) {
            CollectWide(it + b.first + 8, b.second - 8, fe.names);
        }
    }
}

std::vector<FavEntry> ParseFavorites(const std::vector<BYTE>& b) {
    std::vector<FavEntry> res;
    size_t n = b.size(), i = 0;
    while (i < n && res.size() < 512) {
        if (b[i] == 0xFF) break;
        ++i;
        if (i + 4 > n) break;
        size_t sz = RdU32(b.data() + i);
        i += 4;
        if (sz < 2 || sz > n - i) break;
        FavEntry fe;
        size_t j = 0;
        while (j + 2 <= sz) {
            size_t cb = RdU16(b.data() + i + j);
            if (cb == 0) break;
            if (cb < 4 || j + cb > sz) break;
            ParseShellItem(b.data() + i + j, cb, fe);
            j += cb;
        }
        res.push_back(std::move(fe));
        i += sz;
    }
    return res;
}

// Case-insensitive UTF-16LE search inside the blob (fallback only). Returns offset or SIZE_MAX.
size_t FindWide(const std::vector<BYTE>& b, const std::wstring& needle) {
    if (needle.empty() || b.size() < needle.size() * 2) return SIZE_MAX;
    size_t nb = needle.size() * 2;
    for (size_t i = 0; i + nb <= b.size(); ++i) {
        size_t j = 0;
        for (; j < needle.size(); ++j) {
            wchar_t c = (wchar_t)(b[i + j * 2] | (b[i + j * 2 + 1] << 8));
            if (towlower(c) != towlower(needle[j])) break;
        }
        if (j == needle.size()) return i;
    }
    return SIZE_MAX;
}

std::wstring FileNameOf(const std::wstring& p) {
    size_t s = p.find_last_of(L"\\/");
    return s == std::wstring::npos ? p : p.substr(s + 1);
}

// The program behind an AppUserModelID (only used to recognise the windows of a pin that has no target of its own).
std::wstring ResolveAumidExe(const std::wstring& aumid) {
    static std::unordered_map<std::wstring, std::wstring> cache;
    if (aumid.empty() || aumid.size() > 400) return L"";
    std::wstring k = Lower(aumid);
    auto f = cache.find(k);
    if (f != cache.end()) return f->second;
    std::wstring r;
    if (k == L"microsoft.windows.explorer") {
        WCHAR w[MAX_PATH] = {};
        UINT n = GetWindowsDirectoryW(w, MAX_PATH);
        if (n && n < MAX_PATH) r = std::wstring(w) + L"\\explorer.exe";
    } else {
        Com<IShellItem> si;
        Com<IShellItem2> si2;
        if (SUCCEEDED(SHCreateItemFromParsingName((L"shell:AppsFolder\\" + aumid).c_str(), nullptr, IID_PPV_ARGS(si.put()))) &&
            SUCCEEDED(si->QueryInterface(IID_PPV_ARGS(si2.put())))) {
            PWSTR t = nullptr;
            if (SUCCEEDED(si2->GetString(kPkeyLinkTarget, &t)) && t) {
                if (PathFileExistsW(t)) r = t;
                CoTaskMemFree(t);
            }
        }
    }
    if (cache.size() > 300) cache.clear();
    cache[k] = r;
    return r;
}
void FillMexe(Pin& p) {
    if (p.exe.empty() && p.mexe.empty() && !p.aumid.empty()) p.mexe = ResolveAumidExe(p.aumid);
}
std::wstring PinExeNorm(const Pin& p) { return NormPath(!p.exe.empty() ? p.exe : p.mexe); }
// Two pins that are the same application (one from a shortcut file, one from the AppID list of the taskbar layout, ...).
bool SamePin(const Pin& a, const Pin& b) {
    if (!a.key.empty() && a.key == b.key) return true;
    if (!a.aumid.empty() && IEq(a.aumid, b.aumid)) return true;
    if (a.args != b.args) return false;
    std::wstring ea = PinExeNorm(a), eb = PinExeNorm(b);
    if (!ea.empty() && ea == eb) return !IEq(FileNameOf(ea), L"ApplicationFrameHost.exe");
    if (!a.name.empty() && IEq(a.name, b.name) && (ea.empty() || eb.empty())) return true;
    return false;
}
// Removes pins that are the same application as an earlier one (position and key of the first are kept, shortcut data is completed).
std::vector<Pin> DedupePins(std::vector<Pin> in) {
    std::vector<Pin> out;
    for (auto& p : in) {
        bool dup = false;
        for (auto& q : out) {
            if (!SamePin(q, p)) continue;
            if (q.lnk.empty() && !p.lnk.empty()) { q.lnk = p.lnk; q.iconPath = p.iconPath; q.iconIdx = p.iconIdx; }
            if (q.exe.empty()) q.exe = p.exe;
            dup = true;
            break;
        }
        if (!dup) out.push_back(std::move(p));
    }
    return out;
}

// Builds a Pin from an AppID that has no shortcut file (resolved through the AppsFolder).
bool MakeAppIdPin(const std::wstring& id, Pin& p) {
    if (id.empty() || id.size() > 400) return false;
    Com<IShellItem> si;
    if (FAILED(SHCreateItemFromParsingName((L"shell:AppsFolder\\" + id).c_str(), nullptr, IID_PPV_ARGS(si.put())))) return false;
    p.aumid = id;
    p.key = Lower(id);
    PWSTR nm = nullptr;
    if (SUCCEEDED(si->GetDisplayName(SIGDN_NORMALDISPLAY, &nm)) && nm) { p.name = nm; CoTaskMemFree(nm); }
    if (p.name.empty()) p.name = id;
    Com<IShellItem2> si2;
    if (SUCCEEDED(si->QueryInterface(IID_PPV_ARGS(si2.put())))) {
        PWSTR t = nullptr;
        if (SUCCEEDED(si2->GetString(kPkeyLinkTarget, &t)) && t) {
            if (PathFileExistsW(t)) p.exe = t;
            CoTaskMemFree(t);
        }
    }
    FillMexe(p);
    return true;
}

std::vector<Pin> OrderByFavorites(std::vector<Pin> pins) {
    std::vector<BYTE> blob;
    if (!ReadFavoritesBlob(blob)) return pins;
    std::vector<FavEntry> ent = ParseFavorites(blob);
    std::vector<int> pinEnt(pins.size(), -1), entPin(ent.size(), -1);
    auto hasName = [](const FavEntry& e, const std::wstring& s) {
        if (s.empty()) return false;
        for (auto& n : e.names) if (IEq(n, s)) return true;
        return false;
    };
    int matched = 0;
    for (int stage = 1; stage <= 5; ++stage) {
        for (size_t pi = 0; pi < pins.size(); ++pi) {
            if (pinEnt[pi] >= 0) continue;
            const Pin& p = pins[pi];
            std::wstring lnkName = FileNameOf(p.lnk), exeName = FileNameOf(p.exe);
            for (size_t ei = 0; ei < ent.size(); ++ei) {
                if (entPin[ei] >= 0 || ent[ei].retired) continue;
                const FavEntry& e = ent[ei];
                bool ok = false;
                switch (stage) {
                    case 1: ok = hasName(e, lnkName); break;
                    case 2: ok = !p.aumid.empty() && IEq(e.appId, p.aumid); break;
                    case 3: ok = hasName(e, p.name); break;
                    case 4: ok = !p.exe.empty() && (hasName(e, p.exe) || IEq(e.appId, p.exe)); break;
                    case 5: ok = !exeName.empty() && (hasName(e, exeName) || IEq(e.appId, exeName)); break;
                }
                if (ok) { pinEnt[pi] = (int)ei; entPin[ei] = (int)pi; ++matched; break; }
            }
        }
    }
    if (matched == 0) {  // layout not understood: old text-search heuristic on the raw blob
        std::vector<std::pair<size_t, size_t>> ord;
        for (size_t i = 0; i < pins.size(); ++i) {
            size_t off = FindWide(blob, FileNameOf(pins[i].lnk));
            if (off == SIZE_MAX && !pins[i].aumid.empty()) off = FindWide(blob, pins[i].aumid);
            if (off == SIZE_MAX) off = FindWide(blob, pins[i].name);
            ord.push_back({off, i});
        }
        std::stable_sort(ord.begin(), ord.end(), [](auto& a, auto& b) { return a.first < b.first; });
        std::vector<Pin> sorted;
        for (auto& o : ord) sorted.push_back(pins[o.second]);
        return sorted;
    }
    std::vector<Pin> res;
    std::unordered_set<std::wstring> keys;
    for (size_t ei = 0; ei < ent.size(); ++ei) {
        if (entPin[ei] >= 0) {
            Pin& p = pins[entPin[ei]];
            keys.insert(p.key);
            res.push_back(p);
        } else if (!ent[ei].retired && !ent[ei].appId.empty() && !keys.count(Lower(ent[ei].appId))) {
            Pin p;
            if (MakeAppIdPin(ent[ei].appId, p)) {
                int twin = -1;  // the same application as a shortcut pin: that pin takes this position (it is not listed a second time at the end)
                for (size_t qi = 0; qi < pins.size() && twin < 0; ++qi) if (SamePin(pins[qi], p)) twin = (int)qi;
                if (twin < 0) { keys.insert(p.key); res.push_back(std::move(p)); }
                else if (pinEnt[twin] < 0) { pinEnt[twin] = (int)ei; keys.insert(pins[twin].key); res.push_back(pins[twin]); }
            }
        }
    }
    for (size_t pi = 0; pi < pins.size(); ++pi)
        if (pinEnt[pi] < 0) res.push_back(pins[pi]);
    return res;
}

std::vector<Pin> LoadWindowsPins() {
    std::vector<Pin> out;
    std::wstring dir = PinnedFolder();
    if (dir.empty()) return out;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*.lnk").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            Pin p;
            p.lnk = dir + L"\\" + fd.cFileName;
            p.name = BaseNameNoExt(fd.cFileName);
            Com<IShellLinkW> sl;
            if (SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(sl.put())))) {
                Com<IPersistFile> pf;
                if (SUCCEEDED(sl->QueryInterface(IID_PPV_ARGS(pf.put()))) && SUCCEEDED(pf->Load(p.lnk.c_str(), STGM_READ))) {
                    WCHAR path[MAX_PATH * 2] = {}, args[2048] = {}, ico[MAX_PATH * 2] = {};
                    sl->GetPath(path, ARRAYSIZE(path), nullptr, SLGP_RAWPATH);
                    sl->GetArguments(args, ARRAYSIZE(args));
                    int ii = 0;
                    sl->GetIconLocation(ico, ARRAYSIZE(ico), &ii);
                    p.exe = Expand(path);
                    p.iconPath = Expand(ico);
                    p.iconIdx = ii;
                    Com<IPropertyStore> ps;
                    if (SUCCEEDED(sl->QueryInterface(IID_PPV_ARGS(ps.put())))) p.aumid = GetAumidFromStore(ps.get());
                    std::wstring a = Lower(args);
                    p.args = a;
                    if (a.find(L"shell:") != std::wstring::npos || a.find(L"appsfolder") != std::wstring::npos) p.exe.clear();
                    if (p.aumid.empty() && p.exe.empty()) {
                        PIDLIST_ABSOLUTE pidl = nullptr;
                        if (SUCCEEDED(sl->GetIDList(&pidl)) && pidl) {
                            PWSTR nm = nullptr;
                            if (SUCCEEDED(SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, &nm)) && nm) {
                                std::wstring s = nm;
                                if (s.find(L'!') != std::wstring::npos && s.find(L'\\') == std::wstring::npos) p.aumid = s;
                                else if (PathFileExistsW(s.c_str())) p.exe = s;
                                CoTaskMemFree(nm);
                            }
                            CoTaskMemFree(pidl);
                        }
                    }
                }
            }
            if (p.aumid.empty() && p.exe.empty()) continue;
            p.key = !p.aumid.empty() ? Lower(p.aumid) : Lower(p.exe);
            FillMexe(p);
            bool dup = false;
            for (auto& q : out) if (q.key == p.key) dup = true;
            if (!dup) out.push_back(std::move(p));
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    return DedupePins(OrderByFavorites(std::move(out)));
}

// Dock-local overlay on top of the Windows pins (never touches the real taskbar).
// extraPins lines:  key \t a|p \t target \t name
std::vector<Pin> BuildPins() {
    std::vector<Pin> res;
    std::unordered_set<std::wstring> hidden;
    for (auto& k : LoadList(L"hiddenPins")) hidden.insert(k);
    std::unordered_set<std::wstring> have;
    for (auto& p : g_winPins) if (!hidden.count(p.key)) { res.push_back(p); have.insert(p.key); }
    for (auto& line : LoadList(L"extraPins")) {
        std::vector<std::wstring> f;
        size_t i = 0;
        while (true) { size_t j = line.find(L'\t', i); if (j == std::wstring::npos) { f.push_back(line.substr(i)); break; } f.push_back(line.substr(i, j - i)); i = j + 1; }
        if (f.size() < 4 || have.count(f[0])) continue;
        Pin p;
        p.key = f[0];
        if (f[1] == L"a") p.aumid = f[2]; else p.exe = f[2];
        p.name = f[3];
        p.extra = true;
        FillMexe(p);
        bool same = false;
        for (auto& q : res) if (SamePin(q, p)) { same = true; break; }
        if (same) continue;
        have.insert(p.key);
        res.push_back(std::move(p));
    }
    return res;
}

// --- Running windows --------------------------------------------------------
struct WinInfo { DWORD pid = 0; std::wstring aumid, exe; bool provisional = false; };  // provisional: could not be attributed to an app (yet)
std::unordered_map<HWND, WinInfo> g_winCache;

typedef HRESULT(WINAPI* SHGetPropertyStoreForWindow_t)(HWND, REFIID, void**);
typedef LONG(WINAPI* GetAumidProc_t)(HANDLE, UINT32*, PWSTR);

// Image path of a process WITHOUT opening it: an elevated (administrator) program cannot be opened from the non-elevated shell, which used to
// leave its windows without an identity (no icon, no name, no grouping). SystemProcessIdInformation (88) returns the NT path of any process.
std::wstring ExeOfPidNt(DWORD pid) {
    struct SPII { PVOID pid; USHORT len, max; PWSTR buf; };
    typedef LONG(NTAPI * NtQSI_t)(ULONG, PVOID, ULONG, PULONG);
    static NtQSI_t fn = (NtQSI_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation");
    if (!fn) return L"";
    std::vector<WCHAR> buf(1024);
    SPII in{(PVOID)(ULONG_PTR)pid, 0, (USHORT)(buf.size() * sizeof(WCHAR)), buf.data()};
    if (fn(88, &in, sizeof(in), nullptr) != 0 || !in.len) return L"";
    std::wstring nt(buf.data(), in.len / sizeof(WCHAR));
    for (WCHAR d = L'A'; d <= L'Z'; ++d) {  // \Device\HarddiskVolume3\... -> C:\...
        WCHAR dev[3] = {d, L':', 0}, tgt[MAX_PATH] = {};
        if (!QueryDosDeviceW(dev, tgt, MAX_PATH)) continue;
        size_t n = wcslen(tgt);
        if (n && nt.size() > n && nt.compare(0, n, tgt) == 0 && nt[n] == L'\\') return std::wstring(dev) + nt.substr(n);
    }
    return L"";
}
std::wstring ExeOfPid(DWORD pid) {
    std::wstring r;
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (p) {
        WCHAR buf[MAX_PATH * 2];
        DWORD n = ARRAYSIZE(buf);
        if (QueryFullProcessImageNameW(p, 0, buf, &n)) r.assign(buf, n);
        CloseHandle(p);
    }
    if (r.empty() && pid) r = ExeOfPidNt(pid);
    return r;
}
// AppUserModelID of a process (packaged apps, or classic apps with an explicit process AUMID).
std::wstring ProcessAumid(DWORD pid) {
    static GetAumidProc_t fn = (GetAumidProc_t)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetApplicationUserModelId");
    if (!fn) return L"";
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!p) return L"";
    WCHAR buf[256] = {};
    UINT32 n = ARRAYSIZE(buf);
    std::wstring r;
    if (fn(p, &n, buf) == ERROR_SUCCESS && n > 1) r.assign(buf, n - 1);
    CloseHandle(p);
    return r;
}
std::wstring ClassOf(HWND h) {
    WCHAR c[128] = {};
    GetClassNameW(h, c, 128);
    return c;
}

// Last time each window was seen eligible and NOT cloaked. A window that is cloaked only for a moment (for instance a Store app that is just
// being activated) or that is a minimised suspended Store app must not vanish from the Dock - and take its icon with it - while a
// refresh happens to run.
std::unordered_map<HWND, ULONGLONG> g_lastUncloaked;
// Windows that look like real application windows but are not ready yet (still cloaked behind a splash, no title, host not attributed).
// They are re-checked a bounded number of times (g_retryLeft rounds of ~350 ms) instead of being forgotten until some unrelated event.
std::unordered_map<HWND, ULONGLONG> g_pendSeen;
bool g_needRetry = false;
int g_retryLeft = 0;
void NotePending(HWND h) {
    ULONGLONG now = GetTickCount64();
    auto r = g_pendSeen.emplace(h, now);
    if (now - r.first->second < 5000) {
        g_needRetry = true;
        if (r.second) g_retryLeft = 8;
    }
}
// The rules of the Windows taskbar: a visible top-level window with no owner (or WS_EX_APPWINDOW), not a tool window, not cloaked.
// why (optional) receives the reason of a refusal, for the diagnostics.
bool IsTaskbarEligible(HWND h, const wchar_t** why = nullptr) {
    auto no = [&](const wchar_t* r) { if (why) *why = r; return false; };
    if (!IsWindowVisible(h)) return no(L"not visible");
    LONG_PTR ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
    LONG_PTR st = GetWindowLongPtrW(h, GWL_STYLE);
    if ((ex & WS_EX_TOOLWINDOW) && !(ex & WS_EX_APPWINDOW)) return no(L"tool window");
    if (GetWindow(h, GW_OWNER) && !(ex & WS_EX_APPWINDOW)) return no(L"owned window");
    if ((ex & WS_EX_NOACTIVATE) && !(ex & WS_EX_APPWINDOW)) return no(L"no-activate window");
    if (st & WS_CHILD) return no(L"child window");
    std::wstring c = ClassOf(h);
    static const wchar_t* skip[] = {L"Progman", L"WorkerW", L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd", L"Windows.UI.Core.CoreWindow",
                                    L"ForegroundStaging", L"XamlExplorerHostIslandWindow", L"TopLevelWindowForOverflowXamlIsland",
                                    L"WhDockWnd", L"WhDockBackdrop", L"WhDockCtrl", L"WhDockHot", L"NotifyIconOverflowWindow", L"TaskListThumbnailWnd"};
    for (auto sk : skip) if (c == sk) return no(L"shell / system class");
    DWORD cloaked = 0;
    bool isCloaked = SUCCEEDED(DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked;
    if (isCloaked) {
        if (cloaked & 2) return no(L"cloaked by the shell (other virtual desktop)");
        // A minimised Store app is cloaked, too: it stays. A window that is cloaked only for a moment (a Store app behind its splash) is
        // kept if it was seen uncloaked a moment ago, otherwise it is looked at again shortly (NotePending).
        auto f = g_lastUncloaked.find(h);
        bool known = f != g_lastUncloaked.end() && GetTickCount64() - f->second < 4000;
        if (!IsIconic(h) && !known) {
            NotePending(h);
            return no(L"cloaked (not ready yet)");
        }
    }
    // An empty title only means "not ready" for a window that is not clearly a normal one; a captioned window / APPWINDOW is shown anyway.
    if (GetWindowTextLengthW(h) == 0 && !(ex & WS_EX_APPWINDOW) && (st & WS_CAPTION) != WS_CAPTION) {
        NotePending(h);
        return no(L"no title and no caption");
    }
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid == GetCurrentProcessId() && (st & WS_CAPTION) != WS_CAPTION) return no(L"shell-internal window");
    if (!isCloaked) g_lastUncloaked[h] = GetTickCount64();
    return true;
}

BOOL CALLBACK FindForeignChild(HWND c, LPARAM lp) {
    auto* a = (std::pair<DWORD, DWORD>*)lp;  // (frame pid in, hosted pid out)
    DWORD pid = 0;
    GetWindowThreadProcessId(c, &pid);
    if (pid && pid != a->first) { a->second = pid; return FALSE; }
    return TRUE;
}
// Attributes a top-level window to an application: the window's own AppUserModelID, else the process that really hosts it (a Store app's
// ApplicationFrameWindow is owned by ApplicationFrameHost.exe, the app lives in a child window of another process), else its exe.
// A window that cannot be attributed yet returns nullptr for a few seconds (the caller re-checks, see NotePending); after that it is shown
// anyway as a provisional item of its own, never silently dropped. Provisional results are not cached.
const WinInfo* InfoFor(HWND h) {
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    auto it = g_winCache.find(h);
    if (it != g_winCache.end() && it->second.pid == pid && !it->second.provisional) return &it->second;
    WinInfo wi;
    wi.pid = pid;
    static SHGetPropertyStoreForWindow_t fn = (SHGetPropertyStoreForWindow_t)GetProcAddress(GetModuleHandleW(L"shell32.dll"), "SHGetPropertyStoreForWindow");
    if (fn) {
        Com<IPropertyStore> ps;
        if (SUCCEEDED(fn(h, IID_PPV_ARGS(ps.put())))) wi.aumid = GetAumidFromStore(ps.get());
    }
    DWORD exePid = pid;
    bool frame = ClassOf(h) == L"ApplicationFrameWindow";
    if (frame) {
        HWND core = FindWindowExW(h, nullptr, L"Windows.UI.Core.CoreWindow", nullptr);
        if (core) GetWindowThreadProcessId(core, &exePid);
        if (exePid == pid) {  // not a direct child (or not created yet): any descendant owned by another process is the hosted app
            std::pair<DWORD, DWORD> a(pid, 0);
            EnumChildWindows(h, FindForeignChild, (LPARAM)&a);
            if (a.second) exePid = a.second;
        }
    }
    wi.exe = ExeOfPid(exePid);
    if (wi.aumid.empty()) wi.aumid = ProcessAumid(exePid);
    bool unresolved = (frame && wi.aumid.empty() && IEq(FileNameOf(wi.exe), L"ApplicationFrameHost.exe")) || (wi.aumid.empty() && wi.exe.empty());
    if (unresolved) {
        NotePending(h);
        auto seen = g_pendSeen.find(h);
        if (seen != g_pendSeen.end() && GetTickCount64() - seen->second < 3000) return nullptr;
        wi.provisional = true;  // give up waiting: show the window as an item of its own (identified by its handle)
    }
    return &(g_winCache[h] = wi);
}

struct RunGroup { std::wstring key, aumid, exe, title; bool provisional = false; std::vector<HWND> wins; };
BOOL CALLBACK EnumWinProc(HWND h, LPARAM lp) {
    if (IsTaskbarEligible(h)) ((std::vector<HWND>*)lp)->push_back(h);
    return TRUE;
}
std::vector<RunGroup> EnumRunning(std::unordered_map<HWND, int>& zorder) {
    std::vector<HWND> all;
    EnumWindows(EnumWinProc, (LPARAM)&all);
    std::vector<RunGroup> groups;
    std::unordered_map<std::wstring, size_t> idx;
    std::unordered_set<HWND> alive(all.begin(), all.end());
    for (auto it = g_winCache.begin(); it != g_winCache.end();) it = alive.count(it->first) ? std::next(it) : g_winCache.erase(it);
    for (auto it = g_winSeq.begin(); it != g_winSeq.end();) it = alive.count(it->first) ? std::next(it) : g_winSeq.erase(it);
    for (auto it = g_lastUncloaked.begin(); it != g_lastUncloaked.end();) it = alive.count(it->first) ? std::next(it) : g_lastUncloaked.erase(it);
    for (auto it = g_pendSeen.begin(); it != g_pendSeen.end();) it = (alive.count(it->first) || IsWindow(it->first)) ? std::next(it) : g_pendSeen.erase(it);
    int z = 0;
    for (HWND h : all) {
        zorder[h] = z++;
        const WinInfo* wi = InfoFor(h);
        if (!wi) continue;
        if (!g_winSeq.count(h)) g_winSeq[h] = ++g_winSeqCounter;
        std::wstring key = wi->provisional ? L"hwnd:" + std::to_wstring((unsigned long long)(ULONG_PTR)h) : Lower(!wi->aumid.empty() ? wi->aumid : wi->exe);
        auto f = idx.find(key);
        if (f == idx.end()) {
            RunGroup g;
            g.key = key; g.aumid = wi->aumid; g.exe = wi->exe; g.provisional = wi->provisional;
            WCHAR t[256] = {};
            GetWindowTextW(h, t, 256);
            g.title = t;
            idx[key] = groups.size();
            groups.push_back(std::move(g));
            f = idx.find(key);
        }
        groups[f->second].wins.push_back(h);
    }
    return groups;
}

// Diagnostics (setting 'debugLog'): why windows that look like application windows are NOT in the Dock. At most ~40 lines per model change.
BOOL CALLBACK DiagEnumProc(HWND h, LPARAM lp) {
    int& left = *(int*)lp;
    if (left <= 0 || !IsWindowVisible(h)) return TRUE;
    WCHAR t[80] = {};
    GetWindowTextW(h, t, 80);
    if (!t[0]) return TRUE;  // untitled windows are mostly invisible helpers
    const wchar_t* why = L"";
    if (IsTaskbarEligible(h, &why)) return TRUE;
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    --left;
    Wh_Log(L"[apps] NOT shown: \"%ls\" class=%ls exe=%ls reason: %ls", t, ClassOf(h).c_str(), FileNameOf(ExeOfPid(pid)).c_str(), why);
    return TRUE;
}
void LogRunningDiag(const std::vector<RunGroup>& groups) {
    static ULONGLONG winStart = 0;
    ULONGLONG now = GetTickCount64();
    if (now - winStart < 3000) return;  // never more often than every 3 s
    winStart = now;
    for (auto& g : groups) Wh_Log(L"[apps] shown: %ls aumid=%ls (%zu window(s))%ls", g.provisional ? g.title.c_str() : FileNameOf(g.exe).c_str(), g.aumid.c_str(), g.wins.size(), g.provisional ? L" [provisional]" : L"");
    for (auto& p : g_winPins) Wh_Log(L"[pins] %ls | aumid=%ls | exe=%ls | match-exe=%ls | args=%ls | lnk=%ls", p.name.c_str(), p.aumid.c_str(), FileNameOf(p.exe).c_str(), FileNameOf(p.mexe).c_str(), p.args.c_str(), FileNameOf(p.lnk).c_str());
    int left = 40;
    EnumWindows(DiagEnumProc, (LPARAM)&left);
}

std::wstring FileDescription(const std::wstring& path) {
    DWORD dummy = 0, sz = GetFileVersionInfoSizeW(path.c_str(), &dummy);
    if (!sz) return L"";
    std::vector<BYTE> buf(sz);
    if (!GetFileVersionInfoW(path.c_str(), 0, sz, buf.data())) return L"";
    struct LT { WORD l, c; } *tr = nullptr;
    UINT len = 0;
    if (!VerQueryValueW(buf.data(), L"\\VarFileInfo\\Translation", (LPVOID*)&tr, &len) || len < sizeof(LT)) return L"";
    WCHAR q[96];
    wsprintfW(q, L"\\StringFileInfo\\%04x%04x\\FileDescription", tr->l, tr->c);
    LPVOID v = nullptr;
    if (!VerQueryValueW(buf.data(), q, &v, &len) || !v) return L"";
    return (const wchar_t*)v;
}
std::wstring ResolveName(const RunGroup& g) {
    if (g.provisional) return g.title;
    auto c = g_nameCache.find(g.key);
    if (c != g_nameCache.end()) return c->second;
    std::wstring n;
    if (!g.aumid.empty()) {
        Com<IShellItem> si;
        if (SUCCEEDED(SHCreateItemFromParsingName((L"shell:AppsFolder\\" + g.aumid).c_str(), nullptr, IID_PPV_ARGS(si.put())))) {
            PWSTR nm = nullptr;
            if (SUCCEEDED(si->GetDisplayName(SIGDN_NORMALDISPLAY, &nm)) && nm) { n = nm; CoTaskMemFree(nm); }
        }
    }
    if (n.empty() && !g.exe.empty()) n = FileDescription(g.exe);
    if (n.empty() && !g.exe.empty()) n = BaseNameNoExt(g.exe);
    if (n.empty()) n = g.title;
    if (g_nameCache.size() > 400) g_nameCache.clear();
    g_nameCache[g.key] = n;
    return n;
}

HWND RootOf(HWND h) { return h ? GetAncestor(h, GA_ROOTOWNER) : nullptr; }

// Marks the item that owns the foreground window. Returns true when any flag changed.
bool UpdateActiveFlags() {
    HWND fg = GetForegroundWindow();
    HWND root = RootOf(fg);
    DWORD fpid = 0;
    if (fg) GetWindowThreadProcessId(fg, &fpid);
    DockItem* best = nullptr;
    if (fg) {
        for (auto& it : g_items) {
            for (HWND w : it->wins) if (w == root || w == fg) { best = it.get(); break; }
            if (best) break;
        }
        if (!best && fpid && fpid != GetCurrentProcessId()) {  // popup / dialog / child process window of an app
            for (auto& it : g_items) {
                for (HWND w : it->wins) {
                    auto f = g_winCache.find(w);
                    if (f != g_winCache.end() && f->second.pid == fpid) { best = it.get(); break; }
                }
                if (best) break;
            }
        }
    }
    bool changed = false;
    for (auto& it : g_items) {
        bool a = it.get() == best;
        if (it->active != a) { it->active = a; changed = true; }
    }
    return changed;
}

// Builds g_items from pins + running windows + saved order. Returns true if anything visible changed.
std::wstring g_lastSig;
bool RefreshModel(bool reloadPins) {
    if (reloadPins) g_winPins = LoadWindowsPins();
    std::vector<Pin> pins = BuildPins();
    std::unordered_map<HWND, int> zorder;
    std::vector<RunGroup> groups = EnumRunning(zorder);

    std::vector<std::wstring> pinExe;  // normalised exe path of every pin (variables expanded, case / slash / \\?\ differences removed)
    for (auto& p : pins) pinExe.push_back(PinExeNorm(p));
    // Windows of one program that report an AppUserModelID and windows that do not (File Explorer, ...) are one application.
    for (size_t i = 0; i < groups.size(); ++i) {
        RunGroup& g = groups[i];
        if (!g.aumid.empty() || g.exe.empty() || g.provisional || g.wins.empty()) continue;
        std::wstring ge = NormPath(g.exe);
        if (IEq(FileNameOf(ge), L"ApplicationFrameHost.exe")) continue;
        for (size_t j = 0; j < groups.size(); ++j) {
            if (j == i || groups[j].aumid.empty() || groups[j].provisional || groups[j].exe.empty() || NormPath(groups[j].exe) != ge) continue;
            auto& w = groups[j].wins;
            w.insert(w.end(), g.wins.begin(), g.wins.end());
            std::stable_sort(w.begin(), w.end(), [&](HWND a, HWND b) { return zorder[a] < zorder[b]; });
            g.wins.clear();
            break;
        }
    }
    groups.erase(std::remove_if(groups.begin(), groups.end(), [](const RunGroup& g) { return g.wins.empty(); }), groups.end());
    struct Entry { Pin pin; bool pinned = false; std::wstring name; std::vector<HWND> wins; int merged = 0; };
    std::vector<Entry> entries;
    for (auto& p : pins) { Entry e; e.pin = p; e.pinned = true; e.name = p.name; entries.push_back(std::move(e)); }
    std::vector<Entry> extraRun;
    std::unordered_set<std::wstring> liveKeys;
    for (auto& g : groups) {
        liveKeys.insert(g.key);
        int idx = -1;
        if (!g.aumid.empty())
            for (size_t i = 0; i < pins.size(); ++i) if (!pins[i].aumid.empty() && IEq(pins[i].aumid, g.aumid)) { idx = (int)i; break; }
        if (idx < 0 && !g.exe.empty() && !g.provisional) {
            std::wstring ge = NormPath(g.exe);
            int cand = -1;
            for (size_t i = 0; i < pins.size(); ++i) {
                if (pinExe[i].empty() || pinExe[i] != ge) continue;
                if (!pins[i].args.empty() && !pins[i].aumid.empty() && !g.aumid.empty()) continue;  // a different application that merely uses the same program
                if (pins[i].args.empty()) { idx = (int)i; break; }
                if (cand < 0) cand = (int)i;
            }
            if (idx < 0) idx = cand;
        }
        if (idx >= 0) {
            auto& w = entries[idx].wins;
            w.insert(w.end(), g.wins.begin(), g.wins.end());
            ++entries[idx].merged;
        } else {
            Entry e;
            e.pin.key = g.key; e.pin.aumid = g.aumid; e.pin.exe = g.exe;
            e.name = ResolveName(g);
            e.wins = g.wins;
            if (!g_seen.count(g.key)) g_seen[g.key] = ++g_seenCounter;
            extraRun.push_back(std::move(e));
        }
    }
    for (auto it = g_seen.begin(); it != g_seen.end();) it = liveKeys.count(it->first) ? std::next(it) : g_seen.erase(it);
    std::stable_sort(extraRun.begin(), extraRun.end(), [](const Entry& a, const Entry& b) { return g_seen[a.pin.key] < g_seen[b.pin.key]; });
    for (auto& e : extraRun) entries.push_back(std::move(e));
    for (auto& e : entries)
        if (e.merged > 1)
            std::stable_sort(e.wins.begin(), e.wins.end(), [&](HWND a, HWND b) { return zorder[a] < zorder[b]; });

    std::vector<Entry*> shown;
    for (auto& e : entries) {
        bool running = !e.wins.empty();
        if ((e.pinned && S.showPinned) || running) shown.push_back(&e);
    }
    // saved order
    std::unordered_map<std::wstring, size_t> order;
    {
        auto o = LoadList(L"order");
        for (size_t i = 0; i < o.size(); ++i) order[o[i]] = i;
    }
    std::stable_sort(shown.begin(), shown.end(), [&](Entry* a, Entry* b) {
        size_t ia = order.count(a->pin.key) ? order[a->pin.key] : SIZE_MAX;
        size_t ib = order.count(b->pin.key) ? order[b->pin.key] : SIZE_MAX;
        return ia < ib;
    });

    std::unordered_map<std::wstring, ItemPtr> old;
    for (auto& i : g_items) old[i->key] = i;
    std::vector<ItemPtr> next;
    std::wstring sig;
    for (Entry* e : shown) {
        ItemPtr it = old.count(e->pin.key) ? old[e->pin.key] : std::make_shared<DockItem>();
        it->key = e->pin.key;
        it->name = e->name.empty() ? BaseNameNoExt(e->pin.exe) : e->name;
        it->aumid = e->pin.aumid; it->exe = e->pin.exe; it->lnk = e->pin.lnk;
        it->iconPath = e->pin.iconPath; it->iconIdx = e->pin.iconIdx;
        it->pinned = e->pinned; it->extra = e->pin.extra;
        it->wins = e->wins;
        it->stable = e->wins;
        std::stable_sort(it->stable.begin(), it->stable.end(), [](HWND a, HWND b) { return g_winSeq[a] < g_winSeq[b]; });
        if (it->cycleHwnd && std::find(it->wins.begin(), it->wins.end(), it->cycleHwnd) == it->wins.end()) it->cycleHwnd = nullptr;
        if (!it->wins.empty()) it->bounceStart = 0;
        next.push_back(it);
    }
    if (S.showStart) {  // the Start button: a special first item, outside the pin / running model and the saved order
        ItemPtr st = old.count(kStartKey) ? old[kStartKey] : std::make_shared<DockItem>();
        st->key = kStartKey; st->name = L"Start"; st->special = 1;
        next.insert(next.begin(), st);
    }
    g_items = std::move(next);
    ++g_itemsGen;
    UpdateActiveFlags();
    for (auto& it : g_items) {
        sig += it->key + L"|" + it->name + (it->pinned ? L"|P" : L"|U") + (it->wins.empty() ? L"|0" : L"|1") + (it->active ? L"a" : L"n") + std::to_wstring(it->wins.size());
        for (HWND w : it->stable) sig += L"," + std::to_wstring((unsigned long long)(ULONG_PTR)w);  // window identity (z-order independent)
        sig += L";";
    }
    bool changed = sig != g_lastSig;
    g_lastSig = sig;
    if (changed && S.debugLog) LogRunningDiag(groups);
    return changed;
}


// ============================================================================
// 5. Icons: shell extraction -> WIC (premultiplied BGRA, exact pixel size) -> cache
// ============================================================================
struct IconEntry {
    Com<IWICBitmap> bmp;     // null = nothing could be loaded
    bool generic = false;    // stock / failed icon: retried later
    ULONGLONG tick = 0;
    uint32_t gen = 0;        // changes whenever the entry is (re)created -> dock bitmaps are rebuilt
};
std::unordered_map<std::wstring, IconEntry> g_iconCache;  // "itemKey#px" -> entry
uint32_t g_iconGen = 0;
bool g_hasGenericIcons = false;

// WIC factory of the calling thread: the icon worker owns its own factory, the dock thread uses g_wic.
thread_local IWICImagingFactory* t_wic = nullptr;
inline IWICImagingFactory* Wic() { return t_wic ? t_wic : g_wic.get(); }

Com<IWICBitmap> WicFromHBitmap(HBITMAP hb, UINT px) {
    Com<IWICBitmap> out;
    Com<IWICBitmap> raw;
    if (FAILED(Wic()->CreateBitmapFromHBITMAP(hb, nullptr, WICBitmapUseAlpha, raw.put()))) return out;
    Com<IWICBitmapSource> src;
    {
        Com<IWICBitmapSource> s0;
        raw->QueryInterface(IID_PPV_ARGS(s0.put()));
        UINT w = 0, h = 0;
        raw->GetSize(&w, &h);
        if (w != px || h != px) {
            Com<IWICBitmapScaler> sc;
            if (SUCCEEDED(Wic()->CreateBitmapScaler(sc.put())) &&
                SUCCEEDED(sc->Initialize(s0.get(), px, px, WICBitmapInterpolationModeFant)))
                sc->QueryInterface(IID_PPV_ARGS(src.put()));
        }
        if (!src) src = s0;
    }
    Com<IWICFormatConverter> conv;
    if (FAILED(Wic()->CreateFormatConverter(conv.put()))) return out;
    if (FAILED(conv->Initialize(src.get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) return out;
    Wic()->CreateBitmapFromSource(conv.get(), WICBitmapCacheOnLoad, out.put());
    return out;
}
Com<IWICBitmap> WicFromHIcon(HICON ic, UINT px) {
    Com<IWICBitmap> out, raw;
    if (FAILED(Wic()->CreateBitmapFromHICON(ic, raw.put()))) return out;
    Com<IWICBitmapSource> s0;
    raw->QueryInterface(IID_PPV_ARGS(s0.put()));
    UINT w = 0, h = 0;
    raw->GetSize(&w, &h);
    Com<IWICBitmapSource> src = s0;
    if (w != px || h != px) {
        Com<IWICBitmapScaler> sc;
        Com<IWICBitmapSource> s1;
        if (SUCCEEDED(Wic()->CreateBitmapScaler(sc.put())) && SUCCEEDED(sc->Initialize(s0.get(), px, px, WICBitmapInterpolationModeFant)) &&
            SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(s1.put()))))
            src = s1;
    }
    Com<IWICFormatConverter> conv;
    if (FAILED(Wic()->CreateFormatConverter(conv.put()))) return out;
    if (FAILED(conv->Initialize(src.get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) return out;
    Wic()->CreateBitmapFromSource(conv.get(), WICBitmapCacheOnLoad, out.put());
    return out;
}
HBITMAP ShellImage(const std::wstring& parsingName, UINT px) {
    Com<IShellItem> si;
    if (FAILED(SHCreateItemFromParsingName(parsingName.c_str(), nullptr, IID_PPV_ARGS(si.put())))) return nullptr;
    Com<IShellItemImageFactory> f;
    if (FAILED(si->QueryInterface(IID_PPV_ARGS(f.put())))) return nullptr;
    HBITMAP hb = nullptr;
    SIZE sz{(LONG)px, (LONG)px};
    if (FAILED(f->GetImage(sz, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, &hb))) return nullptr;
    return hb;
}
typedef UINT(WINAPI* PrivateExtractIconsW_t)(LPCWSTR, int, int, int, HICON*, UINT*, UINT, UINT);

// ---- icon packs: PNG files from a folder (MyDockFinder style: <exe name>.png) -------------------
struct PackFile { std::wstring light, dark, raw; };  // raw = the file name without extension / _dark (for the word-based matching)
std::unordered_map<std::wstring, PackFile> g_pack;  // normalised name -> files
struct PackEnt { std::wstring key, core, phon; const PackFile* pf = nullptr; };
std::vector<PackEnt> g_packList;  // every pack file with its "core" (noise words removed) and phonetic key, sorted by name
bool g_packBuilt = false;
uint64_t g_packSig = 0;           // signature of the pack folder (file names / sizes / times): a change reloads the icons by itself

// Unicode-aware (the C runtime of a GUI process runs in the "C" locale and does not know Cyrillic letters).
wchar_t LowerCh(wchar_t c) { return (wchar_t)(ULONG_PTR)CharLowerW((LPWSTR)(ULONG_PTR)c); }
// lower case, letters and digits only ("winstore.app" -> "winstoreapp", "ms-teams" -> "msteams")
std::wstring NormKey(const std::wstring& s) {
    std::wstring r;
    for (wchar_t c : s) if (IsCharAlphaNumericW(c)) r += LowerCh(c);
    return r;
}
// Words of a name: split at everything that is not a letter / digit, at lower->UPPER (PhotoshopCC -> photoshop, cc) and at letter <-> digit.
std::vector<std::wstring> NameTokens(const std::wstring& s) {
    std::vector<std::wstring> t;
    std::wstring cur;
    int prev = 0;
    auto cls = [](wchar_t c) { return (c >= L'0' && c <= L'9') ? 3 : IsCharUpperW(c) ? 2 : IsCharAlphaW(c) ? 1 : 0; };
    for (wchar_t c : s) {
        int k = cls(c);
        if (!k) { if (!cur.empty()) t.push_back(cur); cur.clear(); prev = 0; continue; }
        if (!cur.empty() && ((prev == 1 && k == 2) || ((prev == 3) != (k == 3)))) { t.push_back(cur); cur.clear(); }
        cur += LowerCh(c);
        prev = k;
    }
    if (!cur.empty()) t.push_back(cur);
    return t;
}
// "Adobe Photoshop 2024" -> "photoshop": vendor / edition words, versions and one-letter words removed. Falls back to the full key.
std::wstring CoreKey(const std::wstring& raw) {
    static const wchar_t* noise[] = {L"adobe", L"microsoft", L"google", L"app", L"apps", L"desktop", L"x64", L"x86", L"win64", L"win32", L"for",
                                     L"the", L"classic", L"beta", L"preview", L"edition", L"inc", L"cc", L"cs", L"pro", L"exe", L"lnk", L"png"};
    std::wstring r;
    for (auto& w : NameTokens(raw)) {
        if (w.size() < 2 || (w[0] >= L'0' && w[0] <= L'9')) continue;
        bool bad = false;
        for (auto n : noise) if (w == n) { bad = true; break; }
        if (!bad) r += w;
    }
    return r.empty() ? NormKey(raw) : r;
}
// Rough pronunciation key: Cyrillic is transliterated, then spelling variants are folded, so "Photoshop" and "Фотошоп" ("fotoshop"),
// "Discord" and "Дискорд" ("diskord") get the same key.
std::wstring PhonKey(const std::wstring& key) {
    static const wchar_t* cyr[32] = {L"a", L"b", L"v", L"g", L"d", L"e", L"zh", L"z", L"i", L"i", L"k", L"l", L"m", L"n", L"o", L"p",
                                     L"r", L"s", L"t", L"u", L"f", L"h", L"ts", L"ch", L"sh", L"sh", L"", L"i", L"", L"e", L"yu", L"ya"};
    std::wstring t;
    for (wchar_t c : key) {
        if (c >= 0x430 && c <= 0x44F) t += cyr[c - 0x430];
        else if (c == 0x451) t += L'e';
        else t += c;
    }
    auto fold = [&](const wchar_t* from, const wchar_t* to) {
        size_t fl = wcslen(from);
        for (size_t i = t.find(from); i != std::wstring::npos; i = t.find(from, i + wcslen(to))) t.replace(i, fl, to);
    };
    fold(L"sch", L"sh"); fold(L"ch", L"@"); fold(L"ph", L"f"); fold(L"c", L"k"); fold(L"@", L"ch");
    fold(L"w", L"v"); fold(L"y", L"i"); fold(L"x", L"ks"); fold(L"q", L"k"); fold(L"ee", L"i"); fold(L"oo", L"u");
    std::wstring r;
    for (wchar_t c : t) if (r.empty() || r.back() != c) r += c;
    return r;
}
bool WithinOneEdit(const std::wstring& a, const std::wstring& b) {
    if (a == b) return true;
    size_t la = a.size(), lb = b.size();
    if ((la > lb ? la - lb : lb - la) > 1) return false;
    size_t i = 0;
    while (i < la && i < lb && a[i] == b[i]) ++i;
    if (la == lb) return a.compare(i + 1, std::wstring::npos, b, i + 1, std::wstring::npos) == 0;
    return la > lb ? a.compare(i + 1, std::wstring::npos, b, i, std::wstring::npos) == 0 : b.compare(i + 1, std::wstring::npos, a, i, std::wstring::npos) == 0;
}
std::wstring PackFolder() {
    std::wstring f = Expand(S.iconPackFolder);
    while (!f.empty() && (f.back() == L'\\' || f.back() == L'/')) f.pop_back();
    if (!f.empty()) return f;
    WCHAR p[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, p))) return L"";
    return std::wstring(p) + L"\\FloatingDock\\icons";
}
void ScanPackDir(const std::wstring& dir, int depth) {
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    std::vector<std::wstring> subs;
    do {
        std::wstring name = fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (name != L"." && name != L".." && !(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) subs.push_back(name);
            continue;
        }
        size_t dot = name.find_last_of(L'.');
        if (dot == std::wstring::npos || _wcsicmp(name.c_str() + dot, L".png") != 0) continue;
        std::wstring stem = Lower(name.substr(0, dot));
        if (stem.find(L"_mask") != std::wstring::npos) continue;
        bool dark = stem.size() > 5 && stem.compare(stem.size() - 5, 5, L"_dark") == 0;
        std::wstring key = NormKey(dark ? stem.substr(0, stem.size() - 5) : stem);
        if (key.empty()) continue;
        PackFile& pf = g_pack[key];
        std::wstring& slot = dark ? pf.dark : pf.light;
        if (slot.empty()) slot = dir + L"\\" + name;  // earlier folder wins
        if (pf.raw.empty()) pf.raw = dark ? stem.substr(0, stem.size() - 5) : stem;
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    if (depth >= 2) return;
    std::sort(subs.begin(), subs.end(), [](const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
    for (auto& s : subs) ScanPackDir(dir + L"\\" + s, depth + 1);
}
// Cheap signature of the pack folder (two levels deep): names, sizes and write times of the PNG files.
uint64_t PackSigDir(const std::wstring& dir, int depth) {
    uint64_t h = 1469598103934665603ull;
    WIN32_FIND_DATAW fd;
    HANDLE hf = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (hf == INVALID_HANDLE_VALUE) return h;
    std::vector<std::wstring> subs;
    do {
        std::wstring name = fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (name != L"." && name != L".." && !(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) subs.push_back(name);
            continue;
        }
        size_t dot = name.find_last_of(L'.');
        if (dot == std::wstring::npos || _wcsicmp(name.c_str() + dot, L".png") != 0) continue;
        for (wchar_t c : name) { h ^= (uint64_t)c; h *= 1099511628211ull; }
        h ^= ((uint64_t)fd.ftLastWriteTime.dwHighDateTime << 32) | fd.ftLastWriteTime.dwLowDateTime; h *= 1099511628211ull;
        h ^= ((uint64_t)fd.nFileSizeHigh << 32) | fd.nFileSizeLow; h *= 1099511628211ull;
    } while (FindNextFileW(hf, &fd));
    FindClose(hf);
    if (depth < 2)
        for (auto& sd : subs) { h ^= PackSigDir(dir + L"\\" + sd, depth + 1); h *= 1099511628211ull; }
    return h;
}
void BuildPack() {
    g_packBuilt = true;
    g_pack.clear();
    g_packList.clear();
    g_packSig = 0;
    if (!S.useIconPack) return;
    std::wstring f = PackFolder();
    if (!f.empty()) { ScanPackDir(f, 0); g_packSig = PackSigDir(f, 0); }
    for (auto& kv : g_pack) {
        PackEnt e;
        e.key = kv.first;
        e.core = CoreKey(kv.second.raw);
        e.phon = PhonKey(e.core);
        e.pf = &kv.second;
        g_packList.push_back(std::move(e));
    }
    std::sort(g_packList.begin(), g_packList.end(), [](const PackEnt& a, const PackEnt& b) { return a.key < b.key; });
    Wh_Log(L"icon pack: %zu names in %ls", g_pack.size(), f.c_str());
}
void ResetPack() { g_packBuilt = false; g_pack.clear(); g_packList.clear(); }

// Real-name aliases (normalised) for apps whose package / shortcut name differs from their pack file name.
const struct { const wchar_t* from; const wchar_t* to; } kPackAlias[] = {
    {L"windowscalculator", L"calculatorapp"}, {L"windowsnotepad", L"notepad"},
    {L"microsoftedge", L"msedge"}, {L"googlechrome", L"chrome"},
    {L"visualstudiocode", L"code"}, {L"microsoftvisualstudiocode", L"code"},
    {L"windowsstore", L"winstoreapp"}, {L"microsoftstore", L"winstoreapp"}, {L"microsoftwindowsstore", L"winstoreapp"},
    {L"taskmanager", L"taskmgr"}, {L"fileexplorer", L"explorer"}, {L"windowsexplorer", L"explorer"},
    {L"wt", L"windowsterminal"}, {L"microsoftteams", L"msteams"}, {L"teams", L"msteams"},
    {L"immersivecontrolpanel", L"systemsettings"}, {L"windowssettings", L"systemsettings"}, {L"settings", L"systemsettings"},
    {L"telegramdesktop", L"telegram"}, {L"mozillafirefox", L"firefox"}, {L"obsstudio", L"obs"},
    {L"spotifymusic", L"spotify"}, {L"whatsappdesktop", L"whatsapp"},
};
// Every name an app is known by, in priority order (exe, AppUserModelID parts, shortcut, display name) - raw, not normalised.
std::vector<std::wstring> PackNames(const DockItem& it) {
    std::vector<std::wstring> c;
    std::vector<std::wstring> seen;
    auto add = [&](const std::wstring& raw) {
        std::wstring s = NormKey(raw);
        if (s.empty() || std::find(seen.begin(), seen.end(), s) != seen.end()) return;
        seen.push_back(s);
        c.push_back(raw);
        for (auto& a : kPackAlias) if (s == a.from) c.push_back(a.to);
        // obs64.exe / foo32.exe -> obs / foo
        if (s.size() > 4 && (s.compare(s.size() - 2, 2, L"64") == 0 || s.compare(s.size() - 2, 2, L"32") == 0)) c.push_back(s.substr(0, s.size() - 2));
    };
    if (!it.exe.empty()) add(BaseNameNoExt(it.exe));
    if (!it.aumid.empty()) {
        std::wstring a = it.aumid;
        size_t bang = a.find(L'!');
        std::wstring fam = bang == std::wstring::npos ? a : a.substr(0, bang);
        std::wstring app = bang == std::wstring::npos ? L"" : a.substr(bang + 1);
        size_t us = fam.find(L'_');
        if (us != std::wstring::npos) fam.resize(us);
        size_t dot = fam.find_last_of(L'.');
        add(dot == std::wstring::npos ? fam : fam.substr(dot + 1));  // Microsoft.WindowsTerminal -> WindowsTerminal
        if (!app.empty()) {
            size_t d2 = app.find_last_of(L'.');
            std::wstring last = d2 == std::wstring::npos ? app : app.substr(d2 + 1);
            if (!IEq(last, L"App")) add(last);
        }
        add(fam);
    }
    if (!it.lnk.empty()) add(BaseNameNoExt(it.lnk));
    add(it.name);
    return c;
}
// Finds the pack file of an app. From the surest to the loosest rule (the first rule that finds anything wins, names in priority order):
//  1 exact name            photoshop.png        <- Photoshop.exe
//  2 name without noise    "Adobe Photoshop 2024.png" / photoshop.png  (vendor words, versions, CC / Pro... are ignored on both sides)
//  3 same pronunciation    "Фотошоп.png" <- Photoshop,  "Дискорд.png" <- Discord
//  4 one name contains the other (>= 4 letters, at least half of the longer name)   photoshop-cc-2024-icon.png
//  5 one typo / one letter of difference in a pronunciation key (only when a Cyrillic name is involved)
std::wstring PackPathFor(const DockItem& it) {
    if (!S.useIconPack) return L"";
    if (!g_packBuilt) BuildPack();
    if (g_pack.empty()) return L"";
    struct Cand { std::wstring full, core, phon; bool cyr = false; };
    std::vector<Cand> cs;
    for (auto& raw : PackNames(it)) {
        Cand c;
        c.full = NormKey(raw);
        c.core = CoreKey(raw);
        c.phon = PhonKey(c.core);
        for (wchar_t ch : c.full) if (ch > 0x7F) c.cyr = true;
        cs.push_back(std::move(c));
    }
    const PackFile* hit = nullptr;
    int tier = 0;
    for (auto& c : cs) {  // 1
        auto f = g_pack.find(c.full);
        if (f != g_pack.end()) { hit = &f->second; tier = 1; break; }
    }
    for (int t = 2; t <= 5 && !hit; ++t) {
        for (auto& c : cs) {
            if (c.core.size() < 3) continue;
            const PackEnt* best = nullptr;
            float bestRatio = 0;
            for (auto& e : g_packList) {
                bool ok = false;
                float ratio = 1.f;
                if (t == 2) ok = c.core == e.core || c.core == e.key || c.full == e.core;
                else if (t == 3) ok = c.phon.size() >= 4 && c.phon == e.phon;
                else if (t == 4) {
                    const std::wstring& a = c.core.size() <= e.core.size() ? c.core : e.core;
                    const std::wstring& b = c.core.size() <= e.core.size() ? e.core : c.core;
                    if (a.size() >= 4 && b.find(a) != std::wstring::npos && a.size() * 2 >= b.size()) { ok = true; ratio = (float)a.size() / (float)b.size(); }
                } else ok = (c.cyr || std::any_of(e.key.begin(), e.key.end(), [](wchar_t ch) { return ch > 0x7F; })) && c.phon.size() >= 7 && e.phon.size() >= 7 && WithinOneEdit(c.phon, e.phon);
                if (ok && (!best || ratio > bestRatio)) { best = &e; bestRatio = ratio; }
            }
            if (best) { hit = best->pf; tier = t; break; }
        }
    }
    if (S.debugLog) {
        if (hit) Wh_Log(L"[icons] %ls -> %ls (rule %d)", it.name.c_str(), (S.packDark && !hit->dark.empty() ? hit->dark : !hit->light.empty() ? hit->light : hit->dark).c_str(), tier);
        else {
            std::wstring names;
            for (auto& c : cs) names += (names.empty() ? L"" : L", ") + c.core;
            Wh_Log(L"[icons] %ls: no pack file; names tried: %ls", it.name.c_str(), names.c_str());
        }
    }
    if (!hit) return L"";
    if (S.packDark) return !hit->dark.empty() ? hit->dark : hit->light;
    return !hit->light.empty() ? hit->light : hit->dark;
}
// Any WIC source -> px x px premultiplied BGRA, aspect ratio preserved, centred (no stretching).
Com<IWICBitmap> WicSquareFit(IWICBitmapSource* in, UINT px) {
    Com<IWICBitmap> out;
    UINT w = 0, h = 0;
    if (!in || FAILED(in->GetSize(&w, &h)) || !w || !h) return out;
    Com<IWICFormatConverter> conv;
    if (FAILED(Wic()->CreateFormatConverter(conv.put())) ||
        FAILED(conv->Initialize(in, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)))
        return out;
    float sc = (float)px / (float)std::max(w, h);
    UINT nw = std::max<UINT>(1, (UINT)lroundf(w * sc)), nh = std::max<UINT>(1, (UINT)lroundf(h * sc));
    Com<IWICBitmapScaler> scaler;
    IWICBitmapSource* src = conv.get();
    if (nw != w || nh != h) {
        if (FAILED(Wic()->CreateBitmapScaler(scaler.put())) || FAILED(scaler->Initialize(conv.get(), nw, nh, WICBitmapInterpolationModeFant))) return out;
        src = scaler.get();
    }
    std::vector<BYTE> buf((size_t)px * px * 4, 0);
    UINT ox = (px - nw) / 2, oy = (px - nh) / 2;
    WICRect rc{0, 0, (INT)nw, (INT)nh};
    std::vector<BYTE> tmp((size_t)nw * nh * 4);
    if (FAILED(src->CopyPixels(&rc, nw * 4, (UINT)tmp.size(), tmp.data()))) return out;
    for (UINT y = 0; y < nh; ++y) memcpy(&buf[((size_t)(oy + y) * px + ox) * 4], &tmp[(size_t)y * nw * 4], (size_t)nw * 4);
    Wic()->CreateBitmapFromMemory(px, px, GUID_WICPixelFormat32bppPBGRA, px * 4, (UINT)buf.size(), buf.data(), out.put());
    return out;
}
Com<IWICBitmap> LoadPackBitmap(const std::wstring& path, UINT px) {
    Com<IWICBitmap> out;
    Com<IWICBitmapDecoder> dec;
    if (FAILED(Wic()->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, dec.put()))) return out;
    Com<IWICBitmapFrameDecode> fr;
    if (FAILED(dec->GetFrame(0, fr.put()))) return out;
    return WicSquareFit(fr.get(), px);
}

// Loads the best available icon. 'generic' is set when only a stock/placeholder icon could be produced.
// Runs on the icon worker thread (never on the animation path). 'packPath' was resolved by the dock thread.
Com<IWICBitmap> LoadIconBitmap(const DockItem& it, const std::wstring& packPath, UINT px, bool& generic) {
    Com<IWICBitmap> r;
    generic = false;
    if (!Wic()) return r;
    if (!packPath.empty()) {  // icon pack first (its icons already carry their own rounded background)
        r = LoadPackBitmap(packPath, px);
        if (r) return r;
    }
    auto tryBitmap = [&](const std::wstring& pn) {
        if (r || pn.empty()) return;
        HBITMAP hb = ShellImage(pn, px);
        if (hb) { r = WicFromHBitmap(hb, px); DeleteObject(hb); }
    };
    auto tryCustom = [&]() {
        // custom icon of the shortcut (any file/index except "the exe's first icon", which the shell gives us anyway)
        if (r || it.iconPath.empty() || (it.iconIdx == 0 && !it.exe.empty() && IEq(it.iconPath, it.exe)) || !PathFileExistsW(it.iconPath.c_str())) return;
        static PrivateExtractIconsW_t pei = (PrivateExtractIconsW_t)GetProcAddress(GetModuleHandleW(L"user32.dll"), "PrivateExtractIconsW");
        if (!pei) return;
        HICON ic = nullptr;
        UINT id = 0;
        if (pei(it.iconPath.c_str(), it.iconIdx, px, px, &ic, &id, 1, LR_DEFAULTCOLOR) == 1 && ic) {
            r = WicFromHIcon(ic, px);
            DestroyIcon(ic);
        }
    };
    bool packaged = it.aumid.find(L'!') != std::wstring::npos;
    if (packaged) tryBitmap(L"shell:AppsFolder\\" + it.aumid);
    tryCustom();
    if (!it.aumid.empty() && !packaged) tryBitmap(L"shell:AppsFolder\\" + it.aumid);
    if (!r && !it.exe.empty() && PathFileExistsW(it.exe.c_str())) tryBitmap(it.exe);
    if (!r && !it.lnk.empty()) tryBitmap(it.lnk);
    if (!r) {  // icon of a live window
        for (HWND w : it.wins) {
            DWORD_PTR res = 0;
            HICON ic = nullptr;
            if (SendMessageTimeoutW(w, WM_GETICON, ICON_BIG, 0, SMTO_ABORTIFHUNG, 80, &res) && res) ic = (HICON)res;
            if (!ic) ic = (HICON)GetClassLongPtrW(w, GCLP_HICON);
            if (ic) { r = WicFromHIcon(ic, px); if (r) break; }
        }
    }
    if (!r) {
        generic = true;
        SHSTOCKICONINFO si{};
        si.cbSize = sizeof(si);
        if (SUCCEEDED(SHGetStockIconInfo(SIID_APPLICATION, SHGSI_ICON | SHGSI_LARGEICON, &si)) && si.hIcon) {
            r = WicFromHIcon(si.hIcon, px);
            DestroyIcon(si.hIcon);
        }
    }
    return r;
}

std::wstring IconKey(const DockItem& it, UINT px) { return it.key + L"#" + std::to_wstring(px); }

// ============================================================================
// 6. Dock window: layout, magnification, rendering, input
// ============================================================================
constexpr UINT T_HIDE = 2, T_REVEAL = 3, T_WATCH = 4;
constexpr UINT_PTR TC_RETRY = 11, TC_ACT = 12, TC_RETRYRF = 13;
constexpr ULONGLONG kHoverGraceMs = 80;  // the cursor has to stay outside of the hover zone this long before the Dock lets go of it
constexpr UINT WM_APP_APPBAR = WM_APP + 3;
const float kPi = 3.14159265358979f;

struct Rc { float x = 0, w = 0, top = 0, bottom = 0, a = 1.f; };  // a = opacity (an icon that appears / disappears)
struct DBmp {  // Direct2D bitmaps of one icon at several pre-scaled sizes
    Com<ID2D1Bitmap> b[3];
    bool tried[3] = {false, false, false};
    UINT lp[3] = {0, 0, 0};
    UINT px = 0;
    uint32_t gen = 0;
};
// Per-dock, per-item animation state. Lives in a Dock-owned vector that is parallel to g_items and re-synchronised BY ITEM KEY
// only when the model changes (never per frame), so nothing refers to an item by pointer and nothing is allocated per frame.
struct ItemAnim {
    std::wstring key;
    float slide = 0.f;                           // horizontal slide while another icon is dragged
    float pt = 1.f;                              // presence 0..1: an icon that appears grows 0 -> 1, a removed one (ghost) shrinks 1 -> 0
    float ind = 0.f;                             // opacity of the running indicator
    DBmp bm;
};
// An icon whose app was closed (or unpinned) and that is still animating out. It keeps its bitmaps and a place in the row (after the
// item with key 'after'), is drawn but never hit-tested, and is dropped when its animation has finished.
struct Ghost {
    ItemAnim a;
    std::wstring after;                          // key of the nearest preceding item that survived ("" = the row start)
    size_t pos = 0;                              // number of real items before it
};
struct Dock {
    HWND hwnd = nullptr, backdrop = nullptr, edge = nullptr;
    HMONITOR mon = nullptr;
    RECT monRect{};
    UINT dpi = 96;
    float ds = 1.f;
    int winW = 0, winH = 0, winX = 0, winY = 0;
    HDC memDC = nullptr;
    HBITMAP dib = nullptr, oldBmp = nullptr;
    int dibW = 0, dibH = 0;
    Com<ID2D1DCRenderTarget> rt;
    Com<ID2D1SolidColorBrush> brush;
    Com<IDWriteTextFormat> tf;
    Com<IDWriteInlineObject> ellipsis;
    std::vector<ItemAnim> ia, iaSpare;           // parallel to g_items (see SyncAnim)
    std::vector<Ghost> ghosts;                   // icons animating out, sorted by pos
    std::vector<Rc> grects;                      // their rectangles (parallel to ghosts)
    std::vector<float> bl, pe;                   // scratch of Layout: un-magnified left edge of every real item / presence of every element
    std::vector<int> seq;                        // scratch of Layout: row order (>= 0 real item, < 0 ghost -(i+1))
    bool needReflow = false;                     // the number of elements (ghosts) changed: window geometry has to be recomputed
    uint32_t animGen = 0;
    bool bmpsDirty = true;                       // D2D bitmaps have to be (re)built (outside of the frame path)
    int failStreak = 0;
    ULONGLONG errUntil = 0;
    int lblIdx = -1;
    float ib = 48, gap = 8, padX = 10, vpad = 10, baseLeft = 0, baseW = 0, itemsLeft0 = 0, plateH = 0, shadowM = 0;
    float plateL = 0, plateR = 0, plateT = 0, plateB = 0;
    float itemsTop = 0;                          // top edge of the highest (magnified) icon of the last layout
    UINT iconPx = 64;
    int levels = 1;
    UINT levelPx[3] = {64, 64, 64};
    // magnification state: smoothed cursor (critically damped spring), zoom envelope
    float sx = 0, env = 0;
    float su = 0;                                // smoothed cursor that drives the layout (sx + optional extra low-pass)
    float zoom = 0;                              // eased envelope 0..1 of the whole effect
    bool magPrev = false;
    // hover zone (window client coordinates, computed with the geometry; see InHoverZone)
    float zoneL = 0, zoneR = 0, zoneT = 0, zoneB = 0;
    ULONGLONG hoverOutSince = 0;                 // tick count when the cursor left the zone (0 = inside)
    bool pressStartOpen = false;                 // the Start menu was open when the Start button was pressed
    // name label
    std::wstring lblKey, lblHotKey, lblLayoutText;
    ULONGLONG lblSince = 0;
    double lblT = 0;
    float lblAlpha = 0, lblX = 0, lblY = 0, lblW = 0, lblH = 0, lblDt = 0.016f;
    bool lblSnap = false, lblMore = false;
    Com<IDWriteTextLayout> lblLayout;
    std::vector<Rc> rects;
    bool hovering = false, tracking = false;
    float cx = 0, cy = 0;
    int hot = -1;
    int pressIdx = -1;
    std::wstring pressKey;
    POINT pressPt{};
    bool dragging = false, removeHint = false;
    float dragX = 0, dragY = 0;
    int dropSlot = -1;
    bool timerOn = false;                        // this dock wants frames from the pacer
    float hideAmt = 0;
    bool wantHidden = false, fsHidden = false, shown = false, edgeArmed = false;
    int menuDepth = 0;
    int bdW = -1, bdH = -1;
    bool abOn = false;
    RECT abWant{};
    ULONGLONG lastWheel = 0;
};
std::vector<std::unique_ptr<Dock>> g_docks;
std::atomic<HWND> g_ctrl{nullptr};
IContextMenu2* g_cm2 = nullptr;
IContextMenu3* g_cm3 = nullptr;
bool g_inMenu = false;
bool g_active = false;  // controller active (dock thread only)

// ---- asynchronous icon loading ------------------------------------------------------------------------
// Shell icon extraction, PNG decoding and WIC scaling happen on ONE worker thread (own STA + own WIC factory). The dock
// thread only enqueues requests (a snapshot of the item) and later receives finished WIC bitmaps through WM_APP_ICONS;
// Direct2D bitmaps are created on the dock thread, right when an icon arrives - never inside an animation frame.
constexpr UINT WM_APP_FRAME = WM_APP + 4, WM_APP_ICONS = WM_APP + 5;
struct IconReq { std::wstring key; UINT px = 0; DockItem snap; std::wstring pack; uint32_t epoch = 0; };
struct IconRes { std::wstring key; Com<IWICBitmap> bmp; bool generic = false; uint32_t epoch = 0; };
CRITICAL_SECTION g_icoCs;
bool g_icoInit = false;
std::deque<IconReq> g_icoQ;            // guarded by g_icoCs
std::vector<IconRes> g_icoDone;        // guarded by g_icoCs
HANDLE g_icoWake = nullptr, g_icoQuit = nullptr, g_icoThread = nullptr;
std::atomic<bool> g_icoNotify{false};  // a WM_APP_ICONS is already in flight
std::unordered_set<std::wstring> g_icoPending;  // dock thread only: keys that are queued / being loaded
uint32_t g_iconEpoch = 1;              // dock thread only: bumped whenever the cache is dropped (stale results are discarded)

DWORD WINAPI IconWorker(LPVOID) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    IWICImagingFactory* wic = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)))) t_wic = wic;
    bool quit = false;
    while (!quit) {
        HANDLE hs[2] = {g_icoQuit, g_icoWake};
        DWORD r = MsgWaitForMultipleObjects(2, hs, FALSE, INFINITE, QS_ALLINPUT);
        if (r == WAIT_OBJECT_0) break;
        if (r == WAIT_OBJECT_0 + 2) {  // STA: shell / COM may need the message pump
            MSG m;
            while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
            continue;
        }
        for (;;) {
            if (WaitForSingleObject(g_icoQuit, 0) == WAIT_OBJECT_0) { quit = true; break; }
            IconReq rq;
            bool have = false;
            EnterCriticalSection(&g_icoCs);
            if (!g_icoQ.empty()) { rq = std::move(g_icoQ.front()); g_icoQ.pop_front(); have = true; }
            LeaveCriticalSection(&g_icoCs);
            if (!have) break;
            IconRes res;
            res.key = rq.key;
            res.epoch = rq.epoch;
            if (t_wic) res.bmp = LoadIconBitmap(rq.snap, rq.pack, rq.px, res.generic);
            else res.generic = true;
            EnterCriticalSection(&g_icoCs);
            g_icoDone.push_back(std::move(res));
            LeaveCriticalSection(&g_icoCs);
            if (!g_icoNotify.exchange(true)) {
                HWND c = g_ctrl.load();
                if (c) PostMessageW(c, WM_APP_ICONS, 0, 0); else g_icoNotify = false;
            }
        }
    }
    t_wic = nullptr;
    if (wic) wic->Release();
    CoUninitialize();
    return 0;
}
bool StartIconWorker() {
    if (g_icoThread) return true;
    if (!g_icoInit) { InitializeCriticalSection(&g_icoCs); g_icoInit = true; }
    g_icoWake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    g_icoQuit = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_icoWake || !g_icoQuit) return false;
    g_icoThread = CreateThread(nullptr, 0, IconWorker, nullptr, 0, nullptr);
    return g_icoThread != nullptr;
}
void StopIconWorker() {
    if (g_icoThread) {
        SetEvent(g_icoQuit);
        if (WaitForSingleObject(g_icoThread, 8000) == WAIT_TIMEOUT) Wh_Log(L"icon worker did not stop in time");
        CloseHandle(g_icoThread);
        g_icoThread = nullptr;
    }
    if (g_icoWake) { CloseHandle(g_icoWake); g_icoWake = nullptr; }
    if (g_icoQuit) { CloseHandle(g_icoQuit); g_icoQuit = nullptr; }
    if (g_icoInit) { EnterCriticalSection(&g_icoCs); g_icoQ.clear(); g_icoDone.clear(); LeaveCriticalSection(&g_icoCs); }
    g_icoPending.clear();
    g_icoNotify = false;
}

// ---- frame pacer ------------------------------------------------------------------------------------------
// WM_TIMER (the old 15 ms SetTimer) is the lowest-priority message, is quantised to the ~15.6 ms system tick and is starved by
// mouse input, which made the zoom stutter exactly while the mouse moves. Instead a small pacer thread sleeps on a
// high-resolution waitable timer until shortly before the next DWM vertical blank (DwmGetCompositionTimingInfo) and then
// POSTs WM_APP_FRAME to the dock thread (posted messages outrank input and timers and are also dispatched inside menus).
// The animation clock is the *display time* of the frame (the vblank it is meant for), so dt is a whole number of refresh
// periods. When nothing is animating the thread blocks on an event: no polling, no wake-ups.
struct Pacer {
    HANDLE thread = nullptr, evWake = nullptr, evQuit = nullptr, timer = nullptr;
    std::atomic<bool> active{false};
    std::atomic<LONG> pending{0};
    std::atomic<LONGLONG> simQpc{0}, periodQpc{0}, leadQpc{0};
    LONGLONG freq = 1;
    LONGLONG lastSim = 0;       // dock thread only
    double renderMs = 2.0;      // dock thread only: moving average of the CPU time of a frame
};
Pacer g_pc;

DWORD WINAPI PacerThread(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
    LONGLONG vb0 = 0, per = 0, lastTarget = 0;
    int age = 1 << 30;
    for (;;) {
        if (!g_pc.active.load()) {
            HANDLE hs[2] = {g_pc.evQuit, g_pc.evWake};
            if (WaitForMultipleObjects(2, hs, FALSE, INFINITE) == WAIT_OBJECT_0) break;
            age = 1 << 30;
            continue;
        }
        LARGE_INTEGER nowL;
        QueryPerformanceCounter(&nowL);
        LONGLONG now = nowL.QuadPart;
        if (age >= 120 || per <= 0) {  // (re)read the compositor's phase and period
            DWM_TIMING_INFO ti{};
            ti.cbSize = sizeof(ti);
            if (SUCCEEDED(DwmGetCompositionTimingInfo(nullptr, &ti)) && ti.qpcRefreshPeriod > 0 && ti.qpcVBlank > 0) {
                vb0 = (LONGLONG)ti.qpcVBlank;
                per = (LONGLONG)ti.qpcRefreshPeriod;
            } else {
                per = g_pc.freq / 60;
                vb0 = now;
            }
            age = 0;
            g_pc.periodQpc.store(per);
        }
        ++age;
        LONGLONG lead = g_pc.leadQpc.load();
        if (lead <= 0) lead = g_pc.freq / 400;
        LONGLONG target = vb0;
        if (target <= now + lead) target += ((now + lead - target) / per + 1) * per;
        while (target <= lastTarget) target += per;
        LONGLONG waitQpc = (target - lead) - now;
        if (waitQpc < 0) waitQpc = 0;
        DWORD r;
        if (g_pc.timer) {
            LARGE_INTEGER due;
            due.QuadPart = -(waitQpc * 10000000LL / g_pc.freq);
            if (due.QuadPart == 0) due.QuadPart = -1;
            SetWaitableTimer(g_pc.timer, &due, 0, nullptr, nullptr, FALSE);
            HANDLE hs[2] = {g_pc.evQuit, g_pc.timer};
            r = WaitForMultipleObjects(2, hs, FALSE, 250);
        } else {
            r = WaitForSingleObject(g_pc.evQuit, (DWORD)std::max<LONGLONG>(1, waitQpc * 1000 / g_pc.freq));
            if (r == WAIT_TIMEOUT) r = WAIT_OBJECT_0 + 1;
        }
        if (r == WAIT_OBJECT_0) break;
        if (r != WAIT_OBJECT_0 + 1 || !g_pc.active.load()) continue;
        lastTarget = target;
        g_pc.simQpc.store(target);
        if (g_pc.pending.exchange(1) == 0) {  // the dock thread is still busy with the previous frame: skip, never queue up
            HWND c = g_ctrl.load();
            if (c) PostMessageW(c, WM_APP_FRAME, 0, 0); else g_pc.pending.store(0);
        }
    }
    return 0;
}
bool StartPacer() {
    if (g_pc.thread) return true;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_pc.freq = f.QuadPart;
    g_pc.active = false; g_pc.pending = 0; g_pc.lastSim = 0;
    g_pc.leadQpc = g_pc.freq / 400;
    g_pc.evWake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    g_pc.evQuit = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_pc.timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /*CREATE_WAITABLE_TIMER_HIGH_RESOLUTION*/, TIMER_MODIFY_STATE | SYNCHRONIZE);
    if (!g_pc.timer) g_pc.timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_MODIFY_STATE | SYNCHRONIZE);
    if (!g_pc.evWake || !g_pc.evQuit) return false;
    g_pc.thread = CreateThread(nullptr, 0, PacerThread, nullptr, 0, nullptr);
    return g_pc.thread != nullptr;
}
void StopPacer() {
    if (g_pc.thread) {
        g_pc.active = false;
        SetEvent(g_pc.evQuit);
        if (WaitForSingleObject(g_pc.thread, 3000) == WAIT_TIMEOUT) Wh_Log(L"pacer thread did not stop in time");
        CloseHandle(g_pc.thread);
        g_pc.thread = nullptr;
    }
    if (g_pc.timer) { CloseHandle(g_pc.timer); g_pc.timer = nullptr; }
    if (g_pc.evWake) { CloseHandle(g_pc.evWake); g_pc.evWake = nullptr; }
    if (g_pc.evQuit) { CloseHandle(g_pc.evQuit); g_pc.evQuit = nullptr; }
}
void PacerWake() {
    if (g_pc.thread && !g_pc.active.exchange(true)) { g_pc.lastSim = 0; SetEvent(g_pc.evWake); }
}

// While any window procedure of ours is on the stack (possibly inside a modal menu / ShellExecute message pump)
// docks must not be destroyed: structural changes are deferred and flushed when the outermost procedure returns.
int g_busy = 0;        // depth of DockWndProc (our windows' procedures)
int g_structural = 0;  // depth of controller operations that walk / modify g_docks
bool g_deferReconcile = false, g_deferSettings = false;
void FlushDeferred();
struct BusyGuard {
    BusyGuard() { ++g_busy; }
    ~BusyGuard() { if (--g_busy == 0 && g_structural == 0) FlushDeferred(); }
};
struct StructGuard {
    StructGuard() { ++g_structural; }
    ~StructGuard() { if (--g_structural == 0 && g_busy == 0) FlushDeferred(); }
};

void OnItemClick(Dock& d, int idx, bool newInstance);
bool StartMenuIsForeground();
void OpenQuickLinkMenu();
void ScheduleRefresh(bool pins);
void ShowItemMenu(Dock& d, int idx, POINT screenPt);
void ReorderItem(Dock& d, int from, int slot);
void UnpinItem(DockItem& it);
void CycleWindows(DockItem& it, bool forward);
void ModelChanged();
void EnsureTimer(Dock& d);
void ScheduleReconcile();

D2D1_COLOR_F Col(uint32_t rgb, float a) {
    return D2D1_COLOR_F{((rgb >> 16) & 0xFF) / 255.f, ((rgb >> 8) & 0xFF) / 255.f, (rgb & 0xFF) / 255.f, a};
}
// Effective maximum scale (strength scales how much of the configured maximum is applied).
float MaxScale() { return S.mag ? 1.f + (S.maxScalePct / 100.f - 1.f) * (S.magStrength / 100.f) : 1.f; }
// The magnification profile is a 0..1 bell b(t) = (exp(-4 (t/R)^2) - e^-4) / (1 - e^-4), t = |distance| < R, 0 beyond. The Dock row is
// not laid out icon by icon: the whole row is warped by the smooth map X(x) = x + A * BellInt(x - u), where BellInt is the integral of the
// bell (odd, closed form through erf). X is strictly increasing (slope 1 + A*b >= 1), X(u) = u and X' is continuous, so icon positions
// and sizes are smooth functions of the cursor - nothing jumps when the cursor crosses a gap between two icons.
float BellInt(float t, float R) {
    float a = fminf(fabsf(t), R);
    const float e4 = expf(-4.f);
    float v = (R * 0.44311346f * erff(2.f * a / R) - e4 * a) / (1.f - e4);
    return t < 0 ? -v : v;
}
inline float Smooth01(float t) { t = t < 0.f ? 0.f : t > 1.f ? 1.f : t; return t * t * (3.f - 2.f * t); }
inline bool IconAnimOn() { return S.iconAnim && S.iconAnimMs > 0; }
// Re-synchronises the per-item animation state with g_items (matching by item KEY, so add / remove / reorder keep the state of the
// items that survive). Runs only when the model changed; the vectors keep their capacity, nothing is allocated per frame.
// New items start with presence 0 (they grow in); removed ones become ghosts (they shrink out, keeping their bitmaps).
void SyncAnim(Dock& d) {
    size_t n = g_items.size();
    if (d.animGen == g_itemsGen && d.ia.size() == n) return;
    bool animate = IconAnimOn() && !d.ia.empty();  // the first population (and a reset) appears at once
    if (!animate) d.ghosts.clear();
    d.iaSpare.swap(d.ia);
    d.ia.clear();
    d.ia.reserve(std::max(n, d.iaSpare.size()) + 8);
    std::vector<std::wstring> oldKeys;
    oldKeys.reserve(d.iaSpare.size());
    for (auto& o : d.iaSpare) oldKeys.push_back(o.key);
    std::vector<char> used(d.iaSpare.size(), 0);
    for (size_t i = 0; i < n; ++i) {
        const DockItem& it = *g_items[i];
        const std::wstring& key = it.key;
        int from = -1;
        if (i < oldKeys.size() && !used[i] && oldKeys[i] == key) from = (int)i;
        else for (size_t j = 0; j < oldKeys.size(); ++j) if (!used[j] && oldKeys[j] == key) { from = (int)j; break; }
        d.ia.emplace_back();
        ItemAnim& a = d.ia.back();
        if (from >= 0) {
            used[from] = 1;
            a = std::move(d.iaSpare[from]);
            a.key = key;
            continue;
        }
        bool revived = false;  // the app came back while its icon was still shrinking out
        for (size_t g = 0; g < d.ghosts.size(); ++g)
            if (d.ghosts[g].a.key == key) { a = std::move(d.ghosts[g].a); d.ghosts.erase(d.ghosts.begin() + g); revived = true; break; }
        a.key = key;
        if (!revived) {
            a.pt = (animate && !it.special) ? 0.f : 1.f;
            a.ind = it.wins.empty() ? 0.f : 1.f;
        }
    }
    if (animate) {
        for (size_t j = 0; j < d.iaSpare.size(); ++j) {
            if (used[j] || d.iaSpare[j].pt < 0.02f || oldKeys[j] == kStartKey || d.ghosts.size() >= 16) continue;
            Ghost gh;
            for (size_t k = j; k-- > 0;) if (used[k]) { gh.after = oldKeys[k]; break; }
            gh.a = std::move(d.iaSpare[j]);
            gh.a.key = oldKeys[j];
            d.ghosts.push_back(std::move(gh));
        }
    }
    for (auto& gh : d.ghosts) {
        size_t pos = 0;
        bool found = false;
        if (!gh.after.empty()) for (size_t i = 0; i < d.ia.size(); ++i) if (d.ia[i].key == gh.after) { pos = i + 1; found = true; break; }
        gh.pos = (found || gh.after.empty()) ? pos : std::min(gh.pos, d.ia.size());
    }
    std::stable_sort(d.ghosts.begin(), d.ghosts.end(), [](const Ghost& x, const Ghost& y) { return x.pos < y.pos; });
    d.iaSpare.clear();  // releases the bitmaps of items that disappeared (and are not animating out)
    d.rects.reserve(n + 8);
    d.animGen = g_itemsGen;
}
void ResetDeviceBitmaps(Dock& d) {
    for (auto& a : d.ia) a.bm = DBmp{};
    for (auto& g : d.ghosts) g.a.bm = DBmp{};
    d.bmpsDirty = true;
}
float BounceOffset(const DockItem& it, float ib, double now) {
    if (!it.bounceStart) return 0;
    float t = (float)(now - it.bounceStart);
    if (t > 3.5f || t < 0.f) return 0;
    float ph = fmodf(t, 0.7f) / 0.7f;
    return sinf(kPi * ph) * 0.45f * ib * (1.f - t / 3.5f);
}
bool AnyBounce() {
    double now = NowSec();
    for (auto& it : g_items) if (it->bounceStart && now - it->bounceStart < 3.5) return true;
    return false;
}
// ---- geometry --------------------------------------------------------------
// Left edge of item i in the *un-magnified* layout (the layout every magnification calculation is based on).
float BaseLeft(const Dock& d, size_t i) {
    return i < d.bl.size() ? d.bl[i] : d.itemsLeft0 + i * (d.ib + d.gap);  // bl: filled by Layout (icons that appear / disappear change the row)
}
float BaseCenter(const Dock& d, size_t i) { return BaseLeft(d, i) + d.ib * 0.5f; }

void ReflowGeometry(Dock& d) {
    size_t n = g_items.size() + d.ghosts.size();
    d.ds = d.dpi / 96.f;
    float ds = d.ds;
    float ib0 = S.iconSize * ds, gap0 = S.iconSpacing * ds;
    float pad0 = std::max(4.f * ds, (S.dockHeight - S.iconSize) * 0.5f * ds);  // same padding on all four sides
    d.shadowM = roundf((S.shadow ? 30.f : 6.f) * ds);
    float monW = (float)(d.monRect.right - d.monRect.left);
    float ms = MaxScale();
    float span0 = n ? n * ib0 + (n - 1) * gap0 : 0;
    // Room that magnification may need on each side (grows when icons are shrunk to fit).
    auto emaxFor = [&](float ib, float gap) {
        if (!S.mag || !n) return 0.f;
        int cnt = (int)std::min<size_t>(n, (size_t)(2 * S.radius * ds / (ib + gap)) + 2);
        return cnt * (ms - 1.f) * ib;
    };
    float fit = 1.f, limit = monW;
    for (int iter = 0; iter < 8; ++iter) {
        float em = emaxFor(ib0 * fit, gap0 * fit);
        limit = monW * 0.98f - 2 * d.shadowM - 2 * em;
        if (S.fixedMode) limit = std::min(limit, S.fixedWidth * ds);
        float nf = 1.f;
        if (n && span0 + 2 * pad0 > limit) nf = std::clamp(limit / (span0 + 2 * pad0), 0.35f, 1.f);
        if (nf >= fit - 0.004f) break;
        fit = nf;  // monotonically decreasing -> converges
    }
    // whole pixels everywhere: at rest every icon sits exactly on the pixel grid (crisp, no shimmer)
    d.ib = std::max(8.f, roundf(ib0 * fit));
    d.gap = roundf(gap0 * fit);
    d.padX = d.vpad = roundf(pad0 * fit);
    d.plateH = d.ib + 2 * d.vpad;
    float span = n ? n * d.ib + (n - 1) * d.gap : 0;
    d.baseW = roundf(S.fixedMode ? std::max(std::max(limit, 0.f), span + 2 * d.padX) : span + 2 * d.padX);
    float emax = emaxFor(d.ib, d.gap);
    float room = (monW - d.baseW - 2 * d.shadowM) * 0.5f;
    emax = roundf(std::min(emax, std::max(0.f, room)));  // never let the window grow past the monitor
    d.baseLeft = d.shadowM + emax;
    d.itemsLeft0 = roundf(d.baseLeft + (d.baseW - span) * 0.5f);
    // head room above the plate: magnified icons + bounce + gap + name label + its shadow
    float head = d.ib * (ms - 1.f) + 0.5f * d.ib + 54.f * ds;
    d.winW = (int)ceilf(d.baseW + 2 * emax + 2 * d.shadowM);
    d.winH = (int)ceilf(d.shadowM + d.plateH + head);
    d.iconPx = std::max<UINT>(16, (UINT)ceilf(d.ib * ms));
    // pre-scaled bitmap levels (geometric steps): an icon is never down-scaled by more than ~sqrt(maxScale)
    d.levels = ms >= 1.3f ? 3 : ms >= 1.08f ? 2 : 1;
    for (int k = 0; k < d.levels; ++k)
        d.levelPx[k] = d.levels == 1 ? d.iconPx : std::max<UINT>(16, (UINT)lroundf(d.ib * powf(ms, k / (float)(d.levels - 1))));
    d.levelPx[d.levels - 1] = d.iconPx;

    float edge = 16.f * ds;
    float winXf;
    if (S.alignment == 1) winXf = d.monRect.left + edge - d.baseLeft;
    else if (S.alignment == 2) winXf = d.monRect.right - edge - d.baseW - d.baseLeft;
    else winXf = (d.monRect.left + d.monRect.right) * 0.5f - d.winW * 0.5f;
    winXf = std::clamp(winXf, (float)d.monRect.left, std::max((float)d.monRect.left, (float)d.monRect.right - d.winW));
    d.winX = (int)lroundf(winXf);
    d.winY = (int)lroundf(d.monRect.bottom - S.bottomOffset * ds + d.shadowM - d.winH);
    // Hover zone: horizontally the resting plate (X(u) = u and X is monotonic, so the magnified row never needs more), vertically from the
    // top of the biggest icon down to the screen edge. It depends on the geometry only - never on what happens to be magnified right now.
    float pb = (float)d.winH - d.shadowM;
    d.zoneL = d.baseLeft;
    d.zoneR = d.baseLeft + d.baseW;
    d.zoneT = pb - d.vpad - d.ib * ms - 4.f * ds;
    d.zoneB = pb + std::max(4.f * ds, S.bottomOffset * ds);
}

// Computes the on-screen rectangle of every icon and the plate. Icon i occupies [X(b_i), X(b_i + ib)] of the warped row (see BellInt), where
// u is the smoothed cursor and A the current strength of the effect. Stateless: nothing is accumulated from frame to frame.
void Layout(Dock& d) {
    SyncAnim(d);
    size_t n = g_items.size();
    d.rects.assign(n, Rc{});
    d.bl.assign(n, 0.f);
    d.grects.assign(d.ghosts.size(), Rc{});
    d.plateB = (float)d.winH - d.shadowM;
    d.plateT = d.plateB - d.plateH;
    if (!n && d.ghosts.empty()) { d.plateL = d.baseLeft; d.plateR = d.baseLeft + d.baseW; d.itemsTop = d.plateT; return; }
    // row order: ghosts (shrinking icons that were removed) at their place between the real items
    d.seq.clear();
    for (size_t k = 0, gi = 0; k <= n; ++k) {
        while (gi < d.ghosts.size() && d.ghosts[gi].pos <= k) d.seq.push_back(-(int)(++gi));
        if (k < n) d.seq.push_back((int)k);
    }
    size_t N = d.seq.size();
    d.pe.assign(N, 1.f);
    float pitch = d.ib + d.gap, span = 0.f;
    bool allOne = true;
    for (size_t k = 0; k < N; ++k) {
        int sq = d.seq[k];
        float pt = sq >= 0 ? (size_t)sq < d.ia.size() ? d.ia[sq].pt : 1.f : d.ghosts[-sq - 1].a.pt;
        float pe = Smooth01(pt);
        d.pe[k] = pe;
        if (pe < 0.9999f) allOne = false;
        span += pitch * pe;
    }
    span -= d.gap * d.pe[N - 1];
    float L0;
    if (S.fixedMode || S.alignment == 0) L0 = d.baseLeft + (d.baseW - span) * 0.5f;
    else if (S.alignment == 1) L0 = d.baseLeft + d.padX;
    else L0 = d.baseLeft + d.baseW - d.padX - span;
    if (allOne) L0 = roundf(L0);  // at rest every icon sits exactly on the pixel grid
    float R = std::max(1.f, S.radius * d.ds);
    float ms = MaxScale();
    // the icon under the cursor reaches exactly the configured maximum: normalise by the mean of the bell over one icon
    float mean = 2.f * BellInt(d.ib * 0.5f, R) / d.ib;
    float g = mean > 0.05f ? std::min(1.f / mean, 1.5f) : 1.5f;
    float A = (ms - 1.f) * g * d.zoom;
    float u = d.su;
    float iconBottom = d.plateB - d.vpad;
    float itemsL = 1e9f, itemsR = -1e9f, itemsT = 1e9f;
    double now = NowSec();
    float x = L0;
    for (size_t k = 0; k < N; ++k) {
        int sq = d.seq[k];
        float pe = d.pe[k];
        float b = x;
        float l = b, r = b + d.ib * pe;
        if (A > 0.0005f) { l += A * BellInt(l - u, R); r += A * BellInt(r - u, R); }
        x += pitch * pe;
        float w = r - l;
        Rc rc;
        if (sq >= 0) {
            size_t i = (size_t)sq;
            float sl = i < d.ia.size() ? d.ia[i].slide : 0.f;
            float bo = BounceOffset(*g_items[i], d.ib, now);
            d.bl[i] = b;
            rc = Rc{l + sl, w, iconBottom - w - bo, iconBottom - bo, pe};
            d.rects[i] = rc;
        } else {
            rc = Rc{l, w, iconBottom - w, iconBottom, pe};
            d.grects[-sq - 1] = rc;
        }
        itemsL = std::min(itemsL, rc.x);
        itemsR = std::max(itemsR, rc.x + rc.w);
        itemsT = std::min(itemsT, rc.top);
    }
    d.itemsTop = itemsT;
    if (S.fixedMode) {
        d.plateL = std::min(d.baseLeft, itemsL - d.padX);
        d.plateR = std::max(d.baseLeft + d.baseW, itemsR + d.padX);
    } else {
        d.plateL = itemsL - d.padX;
        d.plateR = itemsR + d.padX;
    }
    float lim = d.shadowM * 0.4f;  // keep the plate inside the window (and therefore inside the monitor)
    d.plateL = std::max(d.plateL, lim);
    d.plateR = std::min(d.plateR, (float)d.winW - lim);
}

// Hit test against the *visible* rectangles. The gaps between icons belong half to each neighbour
// (no dead zones while icons move), the separator zone belongs to nobody.
int HitTest(Dock& d, float x, float y) {
    if (d.rects.size() != g_items.size()) return -1;
    float h = d.gap * 0.5f;
    for (size_t i = 0; i < d.rects.size(); ++i) {
        auto& r = d.rects[i];
        if (x >= r.x - h && x < r.x + r.w + h && y >= r.top && y <= d.plateB) return (int)i;
    }
    return -1;
}

// ---- DIB / render target -----------------------------------------------------
bool EnsureDib(Dock& d) {
    if (d.dib && d.dibW == d.winW && d.dibH == d.winH) return true;
    if (d.winW <= 0 || d.winH <= 0) return false;
    if (!d.memDC) {
        HDC s = GetDC(nullptr);
        d.memDC = CreateCompatibleDC(s);
        ReleaseDC(nullptr, s);
        if (!d.memDC) return false;
    }
    if (d.dib) { SelectObject(d.memDC, d.oldBmp); DeleteObject(d.dib); d.dib = nullptr; }
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = d.winW;
    bi.bmiHeader.biHeight = -d.winH;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    d.dib = CreateDIBSection(d.memDC, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!d.dib) return false;
    d.oldBmp = (HBITMAP)SelectObject(d.memDC, d.dib);
    d.dibW = d.winW; d.dibH = d.winH;
    return true;
}
bool EnsureRT(Dock& d) {
    if (d.rt) return true;
    D2D1_RENDER_TARGET_PROPERTIES p = D2D1::RenderTargetProperties(
        D2D1_RENDER_TARGET_TYPE_DEFAULT, D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.f, 96.f,
        D2D1_RENDER_TARGET_USAGE_GDI_COMPATIBLE);
    if (FAILED(g_d2d->CreateDCRenderTarget(&p, d.rt.put()))) return false;
    if (FAILED(d.rt->CreateSolidColorBrush(Col(0, 1), d.brush.put()))) { d.rt.reset(); return false; }
    d.rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    return true;
}
void EnsureTextFormat(Dock& d) {
    if (d.tf) return;
    if (FAILED(g_dw->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                      14.f * d.ds, L"", d.tf.put())))
        return;
    d.tf->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    d.tf->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
    if (SUCCEEDED(g_dw->CreateEllipsisTrimmingSign(d.tf.get(), d.ellipsis.put()))) {
        DWRITE_TRIMMING tr{DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0};
        d.tf->SetTrimming(&tr, d.ellipsis.get());
    }
}

// Frame path: pure lookup of the pre-built bitmap level that fits 'wantPx' best (smallest level >= wantPx: only ever scaled down, by at
// most ~sqrt(max scale)). Nothing is created, decoded or allocated here.
ID2D1Bitmap* PickBitmap(const Dock& d, const DBmp& slot, float wantPx) {
    int lv = d.levels - 1;
    for (int k = 0; k < d.levels; ++k) if ((float)d.levelPx[k] >= wantPx - 0.5f) { lv = k; break; }
    if (slot.b[lv]) return slot.b[lv].get();
    for (int k = d.levels - 1; k >= 0; --k) if (slot.b[k]) return slot.b[k].get();
    return nullptr;
}
ID2D1Bitmap* GetD2DBitmap(const Dock& d, size_t i, float wantPx) {
    if (i >= d.ia.size()) return nullptr;
    return PickBitmap(d, d.ia[i].bm, wantPx);
}
// Event path (reflow / an icon arrived / render target recreated): builds the D2D bitmaps of every icon whose WIC bitmap is ready.
// A level is rebuilt when the cached icon was regenerated (generation counter), the pixel size or the levels changed.
void PrepareBitmaps(Dock& d) {
    d.bmpsDirty = false;
    if (!EnsureRT(d) || !g_wic) { d.bmpsDirty = true; return; }
    SyncAnim(d);
    for (size_t i = 0; i < g_items.size() && i < d.ia.size(); ++i) {
        auto f = g_iconCache.find(IconKey(*g_items[i], d.iconPx));
        if (f == g_iconCache.end() || !f->second.bmp) continue;
        const IconEntry& e = f->second;
        DBmp& slot = d.ia[i].bm;
        bool stale = slot.gen != e.gen || slot.px != d.iconPx;
        for (int k = 0; k < 3 && !stale; ++k) if (slot.lp[k] != d.levelPx[k]) stale = true;
        if (stale) {
            slot = DBmp{};
            slot.gen = e.gen;
            slot.px = d.iconPx;
            for (int k = 0; k < 3; ++k) slot.lp[k] = d.levelPx[k];
        }
        for (int lv = 0; lv < d.levels; ++lv) {
            if (slot.b[lv] || slot.tried[lv]) continue;
            slot.tried[lv] = true;
            if (d.levelPx[lv] == d.iconPx) {
                d.rt->CreateBitmapFromWicBitmap(e.bmp.get(), nullptr, slot.b[lv].put());
            } else {
                Com<IWICBitmapScaler> sc;
                if (SUCCEEDED(g_wic->CreateBitmapScaler(sc.put())) &&
                    SUCCEEDED(sc->Initialize(e.bmp.get(), d.levelPx[lv], d.levelPx[lv], WICBitmapInterpolationModeFant)))
                    d.rt->CreateBitmapFromWicBitmap(sc.get(), nullptr, slot.b[lv].put());
            }
        }
    }
}
// Queues a background load when the icon is missing (or a generic placeholder older than 20 s). Dock thread only.
void RequestIcon(const DockItem& it, UINT px) {
    if (it.special) return;  // the Start button is drawn by the Dock itself
    std::wstring k = IconKey(it, px);
    if (g_icoPending.count(k) || !g_icoThread) return;
    auto f = g_iconCache.find(k);
    bool need = f == g_iconCache.end() || ((f->second.generic || !f->second.bmp) && GetTickCount64() - f->second.tick > 20000);
    if (!need) return;
    IconReq rq;
    rq.key = k;
    rq.px = px;
    rq.snap = it;
    rq.pack = PackPathFor(it);
    rq.epoch = g_iconEpoch;
    g_icoPending.insert(k);
    EnterCriticalSection(&g_icoCs);
    g_icoQ.push_back(std::move(rq));
    LeaveCriticalSection(&g_icoCs);
    SetEvent(g_icoWake);
}
void PrefetchIcons(Dock& d) {
    for (auto& it : g_items) RequestIcon(*it, d.iconPx);
    PrepareBitmaps(d);
}
void Redraw(Dock& d);
void RedrawAll();
// Dock thread, on WM_APP_ICONS: move finished icons into the cache, build their D2D bitmaps, repaint once.
void DrainIconResults() {
    std::vector<IconRes> done;
    g_icoNotify = false;
    EnterCriticalSection(&g_icoCs);
    done.swap(g_icoDone);
    LeaveCriticalSection(&g_icoCs);
    bool changed = false;
    for (auto& r : done) {
        g_icoPending.erase(r.key);
        if (r.epoch != g_iconEpoch) continue;
        IconEntry e;
        e.bmp = std::move(r.bmp);
        e.generic = r.generic || !e.bmp;
        e.tick = GetTickCount64();
        e.gen = ++g_iconGen;
        if (e.generic) g_hasGenericIcons = true;
        g_iconCache[r.key] = std::move(e);
        changed = true;
    }
    if (!changed) return;
    for (auto& dk : g_docks) PrepareBitmaps(*dk);
    RedrawAll();
}
void ResetIconCaches() {  // settings changed: drop everything, discard in-flight results
    ++g_iconEpoch;
    g_iconCache.clear();
    g_icoPending.clear();
    if (g_icoInit) { EnterCriticalSection(&g_icoCs); g_icoQ.clear(); g_icoDone.clear(); LeaveCriticalSection(&g_icoCs); }
    for (auto& dk : g_docks) ResetDeviceBitmaps(*dk);
}
// Drops cached icons that no current item / dock size can use any more.
void PruneIconCaches() {
    std::unordered_set<std::wstring> valid;
    for (auto& dk : g_docks)
        for (auto& it : g_items) valid.insert(IconKey(*it, dk->iconPx));
    for (auto it = g_iconCache.begin(); it != g_iconCache.end();) it = valid.count(it->first) ? std::next(it) : g_iconCache.erase(it);
}

// ---- backdrop (optional blur) -------------------------------------------------
struct ACCENT_POLICY_ { int state; int flags; DWORD color; int anim; };
struct WCAD_ { int attr; PVOID data; SIZE_T size; };
typedef BOOL(WINAPI* SetWCA_t)(HWND, WCAD_*);
void ApplyAccent(HWND h) {
    static SetWCA_t fn = (SetWCA_t)GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowCompositionAttribute");
    if (!fn) return;
    BYTE a = (BYTE)std::clamp(S.opacity * 255 / 100 / 3 + 1, 1, 90);
    uint32_t rgb = S.bg;
    DWORD abgr = ((DWORD)a << 24) | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
    ACCENT_POLICY_ ap{4, 2, abgr, 0};  // ACCENT_ENABLE_ACRYLICBLURBEHIND
    WCAD_ data{19, &ap, sizeof(ap)};    // WCA_ACCENT_POLICY
    fn(h, &data);
}
void UpdateBackdrop(Dock& d, int dy) {
    if (!d.backdrop) return;
    if (!S.blur || !d.shown) { ShowWindow(d.backdrop, SW_HIDE); return; }
    int x = d.winX + (int)lroundf(d.plateL), y = d.winY + dy + (int)lroundf(d.plateT);
    int w = (int)lroundf(d.plateR - d.plateL), h = (int)lroundf(d.plateB - d.plateT);
    if (w < 4 || h < 4) return;
    SetWindowPos(d.backdrop, d.hwnd, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    if (w != d.bdW || h != d.bdH) {
        float r = std::min(S.cornerRadius * d.ds, h * 0.5f);
        HRGN rg = CreateRoundRectRgn(1, 1, w, h, (int)(2 * r), (int)(2 * r));
        if (!SetWindowRgn(d.backdrop, rg, TRUE)) DeleteObject(rg);
        d.bdW = w; d.bdH = h;
    }
}
int HideDy(const Dock& d) { return (int)lroundf(d.hideAmt * (d.plateH + S.bottomOffset * d.ds - 3.f * d.ds)); }
bool PresentDock(Dock& d) {
    int dy = HideDy(d);
    POINT dst{d.winX, d.winY + dy}, src{0, 0};
    SIZE sz{d.winW, d.winH};
    BLENDFUNCTION bf{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
    if (!UpdateLayeredWindow(d.hwnd, nullptr, &dst, &sz, d.memDC, &src, 0, &bf, ULW_ALPHA)) {
        static ULONGLONG lastLog = 0;
        ULONGLONG t = GetTickCount64();
        if (t - lastLog > 10000) { lastLog = t; Wh_Log(L"UpdateLayeredWindow failed (%lu)", GetLastError()); }
        return false;
    }
    UpdateBackdrop(d, dy);
    return true;
}

// ---- rendering ------------------------------------------------------------------
// ---- rendering ------------------------------------------------------------------
void FillRR(Dock& d, float l, float t, float r, float b, float rad, D2D1_COLOR_F c) {
    d.brush->SetColor(c);
    d.rt->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(l, t, r, b), rad, rad), d.brush.get());
}
uint32_t Darken(uint32_t rgb, float k) {
    auto ch = [&](int sh) { return (uint32_t)std::clamp((int)(((rgb >> sh) & 0xFF) * k), 0, 255); };
    return (ch(16) << 16) | (ch(8) << 8) | ch(0);
}
// Text layout + size of a label pill (DPI aware, ellipsis beyond ~320 DIP).
bool MakePill(Dock& d, const std::wstring& text, Com<IDWriteTextLayout>& tl, float& pw, float& ph) {
    if (text.empty() || !d.tf) return false;
    float ds = d.ds;
    tl.reset();
    if (FAILED(g_dw->CreateTextLayout(text.c_str(), (UINT32)text.size(), d.tf.get(), 296.f * ds, 40.f * ds, tl.put()))) return false;
    DWRITE_TEXT_METRICS tm{};
    tl->GetMetrics(&tm);
    pw = ceilf(tm.width) + 1.f + 24.f * ds;
    ph = ceilf(tm.height) + 12.f * ds;
    return true;
}
// Dark rounded pill (same family as the plate): px/py = top-left corner.
void DrawPillAt(Dock& d, IDWriteTextLayout* tl, float px, float py, float pw, float ph, uint32_t bg, float alpha) {
    float ds = d.ds, rad = std::min(11.f * ds, ph * 0.5f);
    for (int i = 3; i >= 1; --i) {  // soft shadow
        float infl = i * 2.2f * ds;
        FillRR(d, px - infl, py - infl + 2.f * ds, px + pw + infl, py + ph + infl + 2.f * ds, rad + infl, Col(0, 0.045f * alpha));
    }
    FillRR(d, px, py, px + pw, py + ph, rad, Col(bg, 0.94f * alpha));
    d.brush->SetColor(Col(0xFFFFFF, 0.13f * alpha));
    d.rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(px + .5f, py + .5f, px + pw - .5f, py + ph - .5f), rad, rad), d.brush.get(), 1.f);
    d.brush->SetColor(Col(0xFFFFFF, alpha));
    d.rt->DrawTextLayout(D2D1::Point2F(px + 12.f * ds, py + 6.f * ds), tl, d.brush.get());
}
void DrawPill(Dock& d, const std::wstring& text, float cx, float bottom, uint32_t bg) {
    Com<IDWriteTextLayout> tl;
    float pw = 0, ph = 0;
    if (!MakePill(d, text, tl, pw, ph)) return;
    float px = std::clamp(cx - pw / 2, 2.f, std::max(2.f, (float)d.winW - pw - 2.f));
    DrawPillAt(d, tl.get(), px, std::max(2.f, bottom - ph), pw, ph, bg, 1.f);
}
// Hover label state machine: short delay before the first label, instant while moving between icons, fade in/out.
// dt = 0 for event-driven repaints (the state then does not advance).
void UpdateLabel(Dock& d, float dt) {
    d.lblDt = dt;
    ULONGLONG tick = GetTickCount64();
    std::wstring hotKey;
    if (S.showLabels && d.hot >= 0 && (size_t)d.hot < g_items.size() && d.hideAmt < 0.5f) hotKey = g_items[d.hot]->key;
    if (hotKey != d.lblHotKey) { d.lblHotKey = hotKey; d.lblSince = tick; }
    bool visible = d.lblAlpha > 0.04f, on = false;
    if (!hotKey.empty()) {
        if (visible || tick - d.lblSince >= (ULONGLONG)S.labelDelay) {
            on = true;
            if (d.lblKey != hotKey) { if (!visible) d.lblSnap = true; d.lblKey = hotKey; }
        } else d.lblMore = true;  // waiting for the delay
    }
    float target = on ? 1.f : 0.f;
    d.lblAlpha += (target - d.lblAlpha) * (1.f - expf(-dt * (14.f + S.animSpeed * 2.f)));
    if (fabsf(target - d.lblAlpha) < 0.01f) d.lblAlpha = target; else d.lblMore = true;
    if (d.lblAlpha <= 0.f && !on) d.lblKey.clear();
}
// Where the label pill goes (smoothed). Part of the model step: the draw call below only paints.
void UpdateLabelPos(Dock& d, float dt) {
    d.lblIdx = -1;
    if (d.lblKey.empty() || d.lblAlpha <= 0.005f) return;
    int idx = -1;
    for (size_t i = 0; i < g_items.size(); ++i) if (g_items[i]->key == d.lblKey) { idx = (int)i; break; }
    if (idx < 0 || (size_t)idx >= d.rects.size()) return;
    const Rc r = d.rects[idx];
    const std::wstring& text = g_items[idx]->name;
    if (!d.lblLayout || d.lblLayoutText != text) {
        if (!MakePill(d, text, d.lblLayout, d.lblW, d.lblH)) { d.lblLayoutText.clear(); return; }
        d.lblLayoutText = text;
    }
    float ds = d.ds, half = d.lblW * 0.5f, mg = 4.f * ds;
    float tx = std::clamp(r.x + r.w * 0.5f, half + mg, std::max(half + mg, (float)d.winW - half - mg));
    float topMin = r.top;  // the label sits above every icon it overlaps horizontally
    for (const Rc& o : d.rects) if (o.x < tx + half && o.x + o.w > tx - half) topMin = std::min(topMin, o.top);
    float ty = std::max(topMin - 9.f * ds, d.lblH + 2.f);
    if (d.lblSnap) { d.lblX = tx; d.lblY = ty; d.lblSnap = false; }
    else {
        d.lblX = tx; d.lblY = ty;  // follows its icon exactly: no catch-up delay
    }
    d.lblIdx = idx;
}
void DrawLabel(Dock& d) {
    if (d.lblIdx < 0 || !d.lblLayout) return;
    DrawPillAt(d, d.lblLayout.get(), d.lblX - d.lblW * 0.5f, d.lblY - d.lblH, d.lblW, d.lblH, Darken(S.bg, 0.55f), d.lblAlpha);
}
// Model -> geometry for one frame: layout from the animated scales, hot item, label. Everything that can allocate / create a D2D
// resource has already happened (PrepareBitmaps, EnsureTextFormat only creates when missing after a reflow).
bool Compose(Dock& d, float dt) {
    if (!InitFactories() || !EnsureDib(d)) return false;
    if (!d.rt || d.bmpsDirty) PrepareBitmaps(d);
    if (!d.rt) return false;
    EnsureTextFormat(d);
    Layout(d);
    if (d.hovering && !d.dragging && !d.menuDepth) d.hot = HitTest(d, d.cx, d.cy);
    else if (!d.hovering || d.dragging) d.hot = -1;
    d.lblMore = false;
    if (!g_items.empty()) { UpdateLabel(d, dt); UpdateLabelPos(d, dt); }
    return true;
}
// The Windows Start button: a soft rounded tile with the four-pane logo (vector, so it stays sharp at every magnification).
void DrawStartButton(Dock& d, const Rc& r, uint32_t indCol) {
    float w = r.w;
    FillRR(d, r.x, r.top, r.x + w, r.bottom, w * 0.22f, indCol == 0xFFFFFF ? Col(0xFFFFFF, 0.13f) : Col(0x000000, 0.10f));
    float logo = w * 0.46f, gp = std::max(1.f, w * 0.045f), p = (logo - gp) * 0.5f;
    float x0 = r.x + (w - logo) * 0.5f, y0 = r.top + (w - logo) * 0.5f, rr = std::max(0.5f, w * 0.012f);
    static const uint32_t cols[4] = {0x58C4FF, 0x35A8FF, 0x2D90F2, 0x1E78E0};
    for (int k = 0; k < 4; ++k) {
        float x = x0 + (k & 1) * (p + gp), y = y0 + (k >> 1) * (p + gp);
        FillRR(d, x, y, x + p, y + p, rr, Col(cols[k], 1.f));
    }
}
// Running indicator: fixed size, fixed row at the bottom of the plate, only its x follows the icon. k = opacity 0..1.
void DrawRunIndicator(Dock& d, float cxm, bool act, float k, uint32_t indCol) {
    if (k <= 0.01f || !(act || S.runningIndicator)) return;
    float ds = d.ds, cy = d.plateB - std::clamp(d.vpad * 0.5f, 2.5f * ds, 5.f * ds);
    float a = (act ? 0.95f : 0.5f) * k;
    d.brush->SetColor(Col(indCol, a));
    if (S.indicatorStyle == 1) {
        float bw = (act ? 16.f : 8.f) * ds, bh = (act ? 3.f : 2.4f) * ds;
        d.rt->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(cxm - bw / 2, cy - bh / 2, cxm + bw / 2, cy + bh / 2), bh / 2, bh / 2), d.brush.get());
        return;
    }
    if (S.indicatorStyle == 2 && act) {
        const float rx[3] = {16, 11, 6}, al[3] = {0.07f, 0.12f, 0.3f};
        for (int g = 0; g < 3; ++g) {
            d.brush->SetColor(Col(indCol, al[g] * k));
            d.rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cxm, cy), rx[g] * ds, (rx[g] * 0.35f) * ds), d.brush.get());
        }
        d.brush->SetColor(Col(indCol, a));
    }
    float rr = (act ? 2.4f : 1.9f) * ds;
    d.rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(cxm, cy), rr, rr), d.brush.get());
}
// Draw + present only (the model / layout / label state was updated by Compose). Returns false on any device / present failure.
bool RenderDock(Dock& d) {
    if (!d.rt || !d.memDC || !d.dib) return false;
    RECT rc{0, 0, d.winW, d.winH};
    if (FAILED(d.rt->BindDC(d.memDC, &rc))) return false;
    float ds = d.ds;
    d.rt->BeginDraw();
    d.rt->Clear(D2D1_COLOR_F{0, 0, 0, 0});
    if (!g_items.empty() || !d.ghosts.empty()) {
        float L = d.plateL, T = d.plateT, R = d.plateR, B = d.plateB;
        float rad = std::min(S.cornerRadius * ds, (B - T) * 0.5f);
        // Hit-test slab. A per-pixel-alpha layered window lets the mouse through every pixel whose alpha is 0, so the gaps between the
        // enlarged icons (above the plate) used to hand the cursor to the window below: WM_MOUSELEAVE, the zoom collapsed, the next pixel
        // brought it back - the Dock "shook" while the cursor travelled over it. Alpha 2/255 is invisible but makes the whole area up to
        // the highest icon solid for the mouse.
        if (d.itemsTop < T - 0.5f) {
            d.brush->SetColor(Col(0x000000, 0.008f));
            d.rt->FillRectangle(D2D1::RectF(L, d.itemsTop - 2.f * ds, R, T + 1.f), d.brush.get());
        }
        if (S.shadow) {
            const int N = 10;
            float step = 2.6f * ds;
            for (int i = 1; i <= N; ++i) {
                float infl = (i - 0.5f) * step;
                float a = 0.10f * powf(1.f - (i - 1) / (float)N, 2.2f);
                d.brush->SetColor(Col(0, a));
                d.rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(L - infl, T - infl + 4 * ds, R + infl, B + infl + 4 * ds), rad + infl, rad + infl),
                                           d.brush.get(), step + 0.6f);
            }
        }
        FillRR(d, L, T, R, B, rad, Col(S.bg, std::max(0.012f, S.opacity / 100.f)));
        if (S.border) {  // thin, low-contrast edge
            d.brush->SetColor(Col(0xFFFFFF, 0.13f));
            d.rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(L + 0.5f, T + 0.5f, R - 0.5f, B - 0.5f), rad, rad), d.brush.get(), std::max(1.f, ds));
        }
        float lum = (0.299f * ((S.bg >> 16) & 0xFF) + 0.587f * ((S.bg >> 8) & 0xFF) + 0.114f * (S.bg & 0xFF)) / 255.f;
        uint32_t indCol = (lum < 0.6f || S.opacity < 25) ? 0xFFFFFF : 0x000000;
        bool haveDrag = d.dragging && d.pressIdx >= 0 && (size_t)d.pressIdx < g_items.size();
        for (size_t i = 0; i < g_items.size() && i < d.rects.size(); ++i) {
            DockItem& it = *g_items[i];
            const Rc& r = d.rects[i];
            bool isDragged = haveDrag && (int)i == d.pressIdx;
            if (isDragged) continue;  // the slot stays empty; the icon follows the cursor (drawn below)
            if (it.special) { DrawStartButton(d, r, indCol); continue; }  // no running indicator, no bitmap
            ID2D1Bitmap* bmp = GetD2DBitmap(d, i, r.w);
            if (bmp) d.rt->DrawBitmap(bmp, D2D1::RectF(r.x, r.top, r.x + r.w, r.bottom), r.a, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            else FillRR(d, r.x, r.top, r.x + r.w, r.bottom, r.w * 0.22f, Col(0xFFFFFF, 0.10f * r.a));
            float ind = i < d.ia.size() ? d.ia[i].ind : (it.wins.empty() ? 0.f : 1.f);
            DrawRunIndicator(d, r.x + r.w * 0.5f, it.active && S.activeIndicator, ind * r.a, indCol);
        }
        // icons of closed apps that are still shrinking out
        for (size_t g = 0; g < d.ghosts.size() && g < d.grects.size(); ++g) {
            const Rc& r = d.grects[g];
            if (r.a <= 0.01f || r.w < 1.f) continue;
            ID2D1Bitmap* bmp = PickBitmap(d, d.ghosts[g].a.bm, r.w);
            if (bmp) d.rt->DrawBitmap(bmp, D2D1::RectF(r.x, r.top, r.x + r.w, r.bottom), r.a, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            else FillRR(d, r.x, r.top, r.x + r.w, r.bottom, r.w * 0.22f, Col(0xFFFFFF, 0.10f * r.a));
            DrawRunIndicator(d, r.x + r.w * 0.5f, false, d.ghosts[g].a.ind * r.a, indCol);
        }
        // dragged icon follows the cursor
        if (haveDrag) {
            float sz = d.ib * 1.12f;
            float cxd = std::clamp(d.dragX, sz / 2, (float)d.winW - sz / 2);
            float cyd = std::clamp(d.dragY, sz / 2 + 2.f, (float)d.winH - d.shadowM - sz / 2);
            ID2D1Bitmap* bmp = GetD2DBitmap(d, (size_t)d.pressIdx, sz);
            if (bmp) d.rt->DrawBitmap(bmp, D2D1::RectF(cxd - sz / 2, cyd - sz / 2, cxd + sz / 2, cyd + sz / 2), d.removeHint ? 0.55f : 0.9f,
                                      D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            if (d.removeHint) DrawPill(d, L"Remove from Dock", cxd, cyd - sz / 2 - 6 * ds, 0x8A1F1F);
        }
        DrawLabel(d);
    }
    HRESULT hr = d.rt->EndDraw();
    if (hr == D2DERR_RECREATE_TARGET) {  // device lost: rebuild the target and the bitmaps (outside the frame path, on the next try)
        d.rt.reset(); d.brush.reset(); d.tf.reset(); d.ellipsis.reset(); d.lblLayout.reset(); d.lblLayoutText.clear();
        ResetDeviceBitmaps(d);
        return false;
    }
    if (FAILED(hr)) return false;
    return PresentDock(d);
}

// ---- AppBar: reserves the strip at the bottom of the monitor so maximised windows stop above the Dock ----
void AppBarUpdate(Dock& d, bool force = false) {
    static bool inside = false;  // SHAppBarMessage pumps sent messages: never re-enter
    if (inside) return;
    struct Flag { Flag() { inside = true; } ~Flag() { inside = false; } } flag;
    bool want = S.reserveWorkArea && S.autoHideWinTaskbar && !S.autoHide && !g_items.empty() && d.hwnd;
    APPBARDATA abd{};
    abd.cbSize = sizeof(abd);
    abd.hWnd = d.hwnd;
    if (!want) {
        if (d.abOn) { SHAppBarMessage(ABM_REMOVE, &abd); d.abOn = false; }
        return;
    }
    RECT rc = d.monRect;
    int h = (int)ceilf(S.bottomOffset * d.ds + d.plateH);
    rc.top = rc.bottom - h;
    if (d.abOn && !force && EqualRect(&rc, &d.abWant)) return;
    if (!d.abOn) {
        abd.uCallbackMessage = WM_APP_APPBAR;
        if (!SHAppBarMessage(ABM_NEW, &abd)) return;
        d.abOn = true;
    }
    abd.uEdge = ABE_BOTTOM;
    abd.rc = rc;
    SHAppBarMessage(ABM_QUERYPOS, &abd);
    abd.rc.top = abd.rc.bottom - h;
    SHAppBarMessage(ABM_SETPOS, &abd);
    d.abWant = rc;
}

// ---- hot edge (auto-hide): a 2 px nearly invisible window along the bottom screen edge ----
void UpdateEdgeWindow(Dock& d) {
    if (!d.edge) return;
    bool want = S.autoHide && S.showOnHover && d.wantHidden && !d.fsHidden && d.shown;
    if (!want) { if (IsWindowVisible(d.edge)) ShowWindow(d.edge, SW_HIDE); return; }
    int x0 = std::max<int>(d.monRect.left, d.winX + (int)d.baseLeft - (int)(60 * d.ds));
    int x1 = std::min<int>(d.monRect.right, d.winX + (int)(d.baseLeft + d.baseW) + (int)(60 * d.ds));
    if (x1 - x0 < 8) { x0 = d.monRect.left; x1 = d.monRect.right; }
    SetWindowPos(d.edge, HWND_TOPMOST, x0, d.monRect.bottom - 2, x1 - x0, 2, SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

// ---- visibility / timers -----------------------------------------------------------
void ApplyVisibility(Dock& d) {
    bool want = (!g_items.empty() || !d.ghosts.empty()) && !d.fsHidden;
    if (want && !d.shown) { ShowWindow(d.hwnd, SW_SHOWNA); d.shown = true; }
    else if (!want && d.shown) { ShowWindow(d.hwnd, SW_HIDE); if (d.backdrop) ShowWindow(d.backdrop, SW_HIDE); d.shown = false; }
}
void EnsureTimer(Dock& d) {
    if (d.hwnd) { d.timerOn = true; PacerWake(); }
}
// A frame that failed (device lost, UpdateLayeredWindow error): retry a few times, then pause instead of looping forever.
void NoteFrameResult(Dock& d, bool ok) {
    if (ok) { d.failStreak = 0; return; }
    if (++d.failStreak >= 3) {
        d.failStreak = 0;
        d.errUntil = GetTickCount64() + 1500;
        d.rt.reset(); d.brush.reset(); ResetDeviceBitmaps(d);
        d.timerOn = false;
        HWND c = g_ctrl.load();
        if (c) SetTimer(c, TC_RETRY, 1600, nullptr);
    }
}
// An icon is still appearing / disappearing (or its running indicator is fading): frames are needed.
bool IconAnimBusy(const Dock& d) {
    if (!d.ghosts.empty() || d.needReflow) return true;
    for (size_t i = 0; i < d.ia.size() && i < g_items.size(); ++i)
        if (d.ia[i].pt < 1.f || d.ia[i].ind != (g_items[i]->wins.empty() ? 0.f : 1.f)) return true;
    return false;
}
// Event-driven repaint (no time step).
void Redraw(Dock& d) {
    if (GetTickCount64() < d.errUntil) return;
    if (d.shown) {
        bool ok = Compose(d, 0.f) && RenderDock(d);
        NoteFrameResult(d, ok);
        if (!ok && d.failStreak > 0) EnsureTimer(d);  // retry on the next frame
    }
    if (d.lblMore || AnyBounce() || IconAnimBusy(d)) EnsureTimer(d);
}
// A drag in progress refers to an item by index: re-resolve it after the model changed.
void FixDragIndex(Dock& d) {
    if (d.pressIdx < 0) return;
    int found = -1;
    for (size_t i = 0; i < g_items.size(); ++i) if (g_items[i]->key == d.pressKey) { found = (int)i; break; }
    if (found >= 0) { d.pressIdx = found; return; }
    d.pressIdx = -1; d.dragging = false; d.removeHint = false; d.dropSlot = -1;
    if (d.hwnd && GetCapture() == d.hwnd) ReleaseCapture();
}
void ReflowDock(Dock& d, bool resetBitmaps = false) {
    FixDragIndex(d);
    d.dpi = 96;
    UINT dx = 96, dy = 96;
    if (d.mon && SUCCEEDED(GetDpiForMonitor(d.mon, MDT_EFFECTIVE_DPI, &dx, &dy)) && dx) d.dpi = dx;
    d.tf.reset(); d.ellipsis.reset(); d.lblLayout.reset(); d.lblLayoutText.clear();
    SyncAnim(d);  // first: the ghosts (icons animating out) are part of the row
    ReflowGeometry(d);
    if (resetBitmaps) ResetDeviceBitmaps(d);
    d.bdW = d.bdH = -1;
    PrefetchIcons(d);  // queue missing icons for the worker, build D2D bitmaps of the ready ones - here, not in a frame
    ApplyVisibility(d);
    AppBarUpdate(d);
    UpdateEdgeWindow(d);
    Redraw(d);
}
// Diagnostics for the hover logic (setting 'debugLog', off by default; rate limited).
bool HoverLogOk() {
    static ULONGLONG winStart = 0;
    static int cnt = 0;
    ULONGLONG t = GetTickCount64();
    if (t - winStart > 10000) { winStart = t; cnt = 0; }
    return ++cnt <= 60;  // at most 60 lines per 10 s: never a log flood
}
#define HOVER_LOG(...) do { if (S.debugLog && HoverLogOk()) Wh_Log(__VA_ARGS__); } while (0)
void SetWantHidden(Dock& d, bool hide);
// One step of the model (time-dependent state only: spring, envelope, slide, hide amount). dt = real time between the display times of
// two consecutive frames.
//
// Magnification pipeline (every stage is a single, documented lag):
//   cursor x (sampled in the frame) = sx
//   sx --[optional extra low-pass, 'smoothing', off by default]--> su
//   su --[warp of the un-magnified row, open loop, see Layout]--> position and size of every icon
//   zoom envelope (enter / leave the Dock, eased) multiplies the strength of the warp, rate = 'animation speed'.
// While the cursor stays inside the hover zone nothing here is ever reset: sx / su / env just keep following the cursor.
bool TickDock(Dock& d, float dt) {
    SyncAnim(d);
    bool more = false;
    size_t n = g_items.size();
    float ms = MaxScale();
    bool magOn = S.mag && ms > 1.001f && d.hovering && !d.dragging && !d.menuDepth;
    if (magOn) {
        if (!d.magPrev && d.env < 0.02f) {
            d.su = d.cx;
            HOVER_LOG(L"[hover] zoom starts from rest (cx=%.0f)", d.cx);
        }
        d.sx = d.cx;  // the layout follows the cursor sampled for THIS frame: no spring, no lag
    } else if (d.magPrev && d.hovering) {
        HOVER_LOG(L"[hover] magnification interrupted while hovering (dragging=%d menu=%d)", (int)d.dragging, d.menuDepth);
    }  // when the cursor left (or a drag / menu started) the position stays where it was while the zoom fades out
    d.magPrev = magOn;
    float et = magOn ? 1.f : 0.f;
    d.env += (et - d.env) * (1.f - expf(-dt * (6.f + S.animSpeed * 2.f)));  // zoom fade: ~0.17 s at the default speed
    if (fabsf(et - d.env) < 0.003f) d.env = et; else more = true;
    d.zoom = d.env * d.env * (3.f - 2.f * d.env);  // ease in / out of the whole effect
    float tau = (S.animSmooth - 1) * 0.012f;        // optional extra low-pass on the layout cursor (0 .. ~0.1 s); 0 by default
    if (tau > 0.0005f) {
        d.su += (d.sx - d.su) * (1.f - expf(-dt / tau));
        if (fabsf(d.sx - d.su) < 0.05f) d.su = d.sx; else more = true;
    } else d.su = d.sx;
    float as = 1.f - expf(-dt * (14.f + S.animSpeed * 1.2f));  // slide animation while dragging
    int from = d.dragging ? d.pressIdx : -1;
    int T = d.dragging && d.dropSlot >= 0 ? d.dropSlot : -1;
    float pitch = d.ib + d.gap;
    for (size_t i = 0; i < n && i < d.ia.size(); ++i) {
        ItemAnim& cur = d.ia[i];
        float st = 0;
        if (from >= 0 && T >= 0 && (int)i != from) {
            if (T > from && (int)i > from && (int)i <= T) st = -pitch;
            else if (T < from && (int)i >= T && (int)i < from) st = pitch;
        }
        cur.slide += (st - cur.slide) * as;
        if (fabsf(st - cur.slide) < 0.4f) cur.slide = st; else more = true;
    }
    {   // icons appearing / disappearing, running indicators
        bool ianim = IconAnimOn();
        float pstep = ianim ? dt / (S.iconAnimMs / 1000.f) : 1.f, istep = 1.f - expf(-dt * 14.f);
        for (size_t i = 0; i < n && i < d.ia.size(); ++i) {
            ItemAnim& a = d.ia[i];
            if (a.pt < 1.f) { a.pt = std::min(1.f, a.pt + pstep); if (a.pt < 1.f) more = true; }
            float tg = g_items[i]->wins.empty() ? 0.f : 1.f;
            if (!ianim) a.ind = tg;
            else if (a.ind != tg) {
                a.ind += (tg - a.ind) * istep;
                if (fabsf(tg - a.ind) < 0.01f) a.ind = tg; else more = true;
            }
        }
        for (size_t g = 0; g < d.ghosts.size();) {
            ItemAnim& a = d.ghosts[g].a;
            a.pt -= pstep;
            a.ind = std::max(0.f, a.ind - pstep * 2.5f);
            if (a.pt <= 0.f) { d.ghosts.erase(d.ghosts.begin() + g); d.needReflow = true; continue; }
            more = true;
            ++g;
        }
    }
    float ht = d.wantHidden ? 1.f : 0.f;
    d.hideAmt += (ht - d.hideAmt) * (1.f - expf(-dt * (4.f + S.animSpeed * 1.5f)));
    if (fabsf(ht - d.hideAmt) < 0.003f) d.hideAmt = ht; else more = true;
    if (AnyBounce() || d.dragging) more = true;
    return more;
}
// ---- hover -----------------------------------------------------------------------------------------------
// Two separate things: the Dock as a whole is hovered (magnification runs, one continuous state) and, inside it, an icon is hot.
// "Dock hovered" is decided from the CURSOR POSITION against a geometric zone, not from window messages: the Dock is a per-pixel-alpha
// layered window, and the system hands the mouse to the window below (WM_MOUSELEAVE) over every pixel whose alpha is 0 - a gap between two
// enlarged icons was enough to drop the zoom for a frame and restart it. Messages only START the hover (WM_MOUSEMOVE inside the zone);
// it ends when the polled cursor has been outside the zone (plus a small margin) for kHoverGraceMs.
bool InHoverZone(const Dock& d, float x, float y, float margin) {
    float l = std::min(d.zoneL, d.plateL), r = std::max(d.zoneR, d.plateR);  // the enlarged plate counts as part of the Dock
    return x >= l - margin && x < r + margin && y >= d.zoneT - margin && y < d.zoneB + margin;
}
void HoverEnter(Dock& d, float x, float y) {
    d.cx = x; d.cy = y;
    d.hoverOutSince = 0;
    if (d.hovering) return;
    d.hovering = true;
    HOVER_LOG(L"[hover] ENTER x=%.0f y=%.0f env=%.2f (%ls)", x, y, d.env, d.env < 0.02f ? L"fresh" : L"continues the fading zoom");
    if (d.hwnd) SetTimer(d.hwnd, T_WATCH, 120, nullptr);
    if (d.hwnd) KillTimer(d.hwnd, T_HIDE);
    if (d.wantHidden) SetWantHidden(d, false);
    EnsureTimer(d);
}
void HoverEnd(Dock& d, const wchar_t* why) {
    if (!d.hovering) return;
    d.hovering = false; d.hot = -1; d.hoverOutSince = 0;
    HOVER_LOG(L"[hover] EXIT (%ls) env=%.2f", why, d.env);
    if (d.hwnd) KillTimer(d.hwnd, T_WATCH);
    EnsureTimer(d);
    if (S.autoHide && !d.menuDepth && d.hwnd) SetTimer(d.hwnd, T_HIDE, 600, nullptr);
}
// Polls the cursor (once per frame and from the T_WATCH timer while no frames run). Returns true while frames are needed.
// WM_MOUSEMOVE is a low-priority, coalesced pseudo-message that the loop serves after the (higher priority) posted frame message, so
// a frame could be built from a position that is a few ms old; the poll gives every frame the current position.
bool HoverUpdate(Dock& d) {
    if (!d.hovering) return false;
    if (d.menuDepth || !d.shown) { HoverEnd(d, d.menuDepth ? L"menu" : L"dock hidden"); return false; }
    if (d.dragging || d.pressIdx >= 0 || (d.hwnd && GetCapture() == d.hwnd)) { d.hoverOutSince = 0; return false; }  // messages drive it
    POINT p;
    if (!GetCursorPos(&p)) return false;
    float x = (float)(p.x - d.winX), y = (float)(p.y - (d.winY + HideDy(d)));
    if (InHoverZone(d, x, y, 6.f * d.ds)) {
        if (d.hoverOutSince) { HOVER_LOG(L"[hover] back inside the zone within the grace time"); d.hoverOutSince = 0; }
        bool moved = fabsf(x - d.cx) > 0.5f || fabsf(y - d.cy) > 0.5f;
        d.cx = x; d.cy = y;
        return moved;
    }
    ULONGLONG now = GetTickCount64();
    if (!d.hoverOutSince) {
        d.hoverOutSince = now;
        HOVER_LOG(L"[hover] cursor left the zone (x=%.0f y=%.0f, zone %.0f..%.0f / %.0f..%.0f): grace %llu ms", x, y, d.zoneL, d.zoneR, d.zoneT, d.zoneB, kHoverGraceMs);
        return true;
    }
    if (now - d.hoverOutSince >= kHoverGraceMs) { HoverEnd(d, L"cursor outside the zone"); return false; }
    return true;
}
// One animation frame of one dock: hover poll -> model step -> geometry -> draw/present. Returns true while the dock still needs frames.
bool StepDock(Dock& d, float dt) {
    bool more = HoverUpdate(d);
    if (TickDock(d, dt)) more = true;
    if (d.needReflow) {  // the last ghost is gone: the window shrinks to the real number of icons
        d.needReflow = false;
        ReflowGeometry(d);
        ApplyVisibility(d);
        AppBarUpdate(d);
        UpdateEdgeWindow(d);
        d.bdW = d.bdH = -1;
    }
    if (GetTickCount64() < d.errUntil) return false;
    if (d.shown) {
        bool ok = Compose(d, dt) && RenderDock(d);
        NoteFrameResult(d, ok);
        if (!ok) return d.timerOn;  // NoteFrameResult stops the dock after repeated failures
        if (d.lblMore) more = true;
    }
    return more;
}
// WM_APP_FRAME: one display-synchronised frame for every dock that is animating.
void OnFrame() {
    g_pc.pending.store(0);
    if (!g_active) { g_pc.active.store(false); g_pc.lastSim = 0; return; }
    BusyGuard bg;
    LONGLONG sim = g_pc.simQpc.load(), per = g_pc.periodQpc.load();
    if (per <= 0) per = g_pc.freq / 60;
    if (g_pc.lastSim && sim <= g_pc.lastSim) return;  // duplicate / out-of-order tick
    double dts = g_pc.lastSim ? (double)(sim - g_pc.lastSim) / (double)g_pc.freq : (double)per / (double)g_pc.freq;
    if (dts > 0.25) dts = (double)per / (double)g_pc.freq;  // resumed after a stall: do not fast-forward
    float dt = std::clamp((float)dts, 0.001f, 0.05f);
    g_pc.lastSim = sim;
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    bool any = false;
    for (size_t i = 0; i < g_docks.size(); ++i) {
        Dock& d = *g_docks[i];
        if (!d.timerOn) continue;
        if (StepDock(d, dt)) any = true; else d.timerOn = false;
    }
    QueryPerformanceCounter(&t1);
    double ms = (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / (double)g_pc.freq;
    g_pc.renderMs = g_pc.renderMs * 0.85 + ms * 0.15;
    LONGLONG lead = (LONGLONG)((g_pc.renderMs * 1.3 + 1.5) * 1e-3 * (double)g_pc.freq);  // wake this long before the vblank
    g_pc.leadQpc.store(std::clamp<LONGLONG>(lead, g_pc.freq / 600, per * 8 / 10));
    if (!any) { g_pc.active.store(false); g_pc.lastSim = 0; }
}
void SetWantHidden(Dock& d, bool hide) {
    if (d.wantHidden != hide) {
        d.wantHidden = hide;
        EnsureTimer(d);
    }
    UpdateEdgeWindow(d);
}
bool CursorOverDock(Dock& d, int grow) {
    POINT p;
    GetCursorPos(&p);
    int dy = HideDy(d);
    RECT r{d.winX + (int)d.plateL - grow, d.winY + dy + (int)d.plateT - grow, d.winX + (int)d.plateR + grow, d.winY + dy + (int)d.plateB + grow};
    return PtInRect(&r, p) != 0;
}
bool IsFullscreenOn(const RECT& mr) {
    HWND fg = GetForegroundWindow();
    if (!fg || !IsWindowVisible(fg)) return false;
    std::wstring c = ClassOf(fg);
    if (c == L"Progman" || c == L"WorkerW" || c == L"Shell_TrayWnd" || c == L"Shell_SecondaryTrayWnd" || c == L"WhDockWnd" || c == L"WhDockHot") return false;
    LONG_PTR st = GetWindowLongPtrW(fg, GWL_STYLE);
    if (((st & WS_CAPTION) == WS_CAPTION) || IsZoomed(fg)) return false;
    RECT r;
    if (!GetWindowRect(fg, &r)) return false;
    return r.left <= mr.left && r.top <= mr.top && r.right >= mr.right && r.bottom >= mr.bottom;
}
void UpdateFullscreen() {
    for (auto& dk : g_docks) {
        bool fs = IsFullscreenOn(dk->monRect);
        if (fs != dk->fsHidden) {
            dk->fsHidden = fs;
            ApplyVisibility(*dk);
            UpdateEdgeWindow(*dk);
            Redraw(*dk);
        }
    }
}
void RedrawAll() {
    for (auto& dk : g_docks) Redraw(*dk);
}

// ---- window procedures ---------------------------------------------------------------------
LRESULT CALLBACK DockWndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    Dock* d = (Dock*)GetWindowLongPtrW(h, GWLP_USERDATA);
    if (m == WM_NCCREATE) {
        SetWindowLongPtrW(h, GWLP_USERDATA, (LONG_PTR)((CREATESTRUCTW*)l)->lpCreateParams);
        return DefWindowProcW(h, m, w, l);
    }
    if (!d) return DefWindowProcW(h, m, w, l);
    BusyGuard busy;
    switch (m) {
        case WM_NCHITTEST: return HTCLIENT;
        case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
        case WM_ERASEBKGND: return 1;
        case WM_PAINT: { PAINTSTRUCT ps; BeginPaint(h, &ps); EndPaint(h, &ps); return 0; }
        case WM_SETCURSOR: SetCursor(LoadCursorW(nullptr, IDC_ARROW)); return TRUE;
        case WM_DPICHANGED: ScheduleReconcile(); return 0;
        case WM_APP_APPBAR:
            if (w == ABN_POSCHANGED) AppBarUpdate(*d, true);
            return 0;
        case WM_INITMENUPOPUP: case WM_DRAWITEM: case WM_MEASUREITEM: case WM_MENUCHAR: {
            LRESULT res = 0;
            if (g_cm3 && SUCCEEDED(g_cm3->HandleMenuMsg2(m, w, l, &res))) return res;
            if (g_cm2 && SUCCEEDED(g_cm2->HandleMenuMsg(m, w, l))) return (m == WM_INITMENUPOPUP) ? 0 : TRUE;
            break;
        }
        case WM_MOUSEMOVE: {
            float x = (float)GET_X_LPARAM(l), y = (float)GET_Y_LPARAM(l);
            bool pressing = d->pressIdx >= 0 && (w & MK_LBUTTON);
            if (!d->tracking) {  // WM_MOUSELEAVE is only used for diagnostics now, it never ends the hover
                TRACKMOUSEEVENT t{sizeof(t), TME_LEAVE, h, 0};
                if (TrackMouseEvent(&t)) d->tracking = true;
            }
            if (!d->hovering) {
                // the faint shadow above the plate / the window rim is not the Dock: a hover starts over the plate (or the icons still fading out)
                if (!pressing && (!InHoverZone(*d, x, y, 0.f) || y < std::min(d->plateT, d->itemsTop) - 2.f * d->ds)) return 0;
                HoverEnter(*d, x, y);
            } else if (pressing || InHoverZone(*d, x, y, 6.f * d->ds)) {
                d->cx = x; d->cy = y; d->hoverOutSince = 0;
            }
            KillTimer(h, T_HIDE);
            if (d->wantHidden) SetWantHidden(*d, false);
            if (pressing) {
                int thr = (int)(6 * d->ds);
                if (!d->dragging && (abs((int)x - d->pressPt.x) > thr || abs((int)y - d->pressPt.y) > thr) &&
                    d->pressIdx < (int)g_items.size() && !g_items[d->pressIdx]->special)  // the Start button cannot be dragged
                    d->dragging = true;
                if (d->dragging) {
                    d->dragX = x; d->dragY = y;
                    int slot = 0;
                    for (size_t i = 0; i < g_items.size(); ++i) {
                        if ((int)i == d->pressIdx) continue;
                        if (BaseCenter(*d, i) < x) ++slot;  // un-magnified centre: stable thresholds
                    }
                    if (!g_items.empty() && g_items[0]->special && d->pressIdx > 0) slot = std::max(slot, 1);  // never in front of the Start button
                    d->dropSlot = slot;
                    d->removeHint = d->pressIdx < (int)g_items.size() && g_items[d->pressIdx]->pinned && y < d->plateT - d->plateH * 0.7f;
                    d->hot = -1;
                }
            }
            EnsureTimer(*d);
            return 0;
        }
        case WM_MOUSELEAVE: {
            // Not a hover exit (see the hover section): the system also reports a leave when the cursor merely crosses a fully transparent
            // pixel of this window. HoverUpdate decides from the cursor position. Here: diagnostics and re-arming only.
            d->tracking = false;
            if (d->hovering && S.debugLog) {
                POINT lp;
                if (GetCursorPos(&lp)) {
                    float lx = (float)(lp.x - d->winX), ly = (float)(lp.y - (d->winY + HideDy(*d)));
                    HOVER_LOG(L"[hover] WM_MOUSELEAVE while hovering: cursor %ls the zone%ls", InHoverZone(*d, lx, ly, 0.f) ? L"INSIDE" : L"outside",
                              InHoverZone(*d, lx, ly, 0.f) ? L" -> false leave, ignored" : L"");
                }
            }
            EnsureTimer(*d);
            return 0;
        }
        case WM_LBUTTONDBLCLK:  // the second click of a double click is a click of its own (OnItemClick de-bounces)
        case WM_LBUTTONDOWN: {
            float x = (float)GET_X_LPARAM(l), y = (float)GET_Y_LPARAM(l);
            d->pressIdx = HitTest(*d, x, y);
            d->pressKey = d->pressIdx >= 0 && d->pressIdx < (int)g_items.size() ? g_items[d->pressIdx]->key : std::wstring();
            d->pressPt = POINT{(LONG)x, (LONG)y};
            d->pressStartOpen = d->pressIdx >= 0 && g_items[d->pressIdx]->special && StartMenuIsForeground();
            d->dragging = false; d->removeHint = false; d->dropSlot = -1;
            if (d->pressIdx >= 0) SetCapture(h);
            return 0;
        }
        case WM_LBUTTONUP: {
            float x = (float)GET_X_LPARAM(l), y = (float)GET_Y_LPARAM(l);
            int pi = d->pressIdx;
            // the pressed item is found again BY KEY: the model may have changed (window opened / closed) between press and release
            if (!d->pressKey.empty()) {
                pi = -1;
                for (size_t i = 0; i < g_items.size(); ++i) if (g_items[i]->key == d->pressKey) { pi = (int)i; break; }
            }
            bool wasDrag = d->dragging, remove = d->removeHint;
            int slot = d->dropSlot;
            d->pressIdx = -1; d->pressKey.clear(); d->dragging = false; d->removeHint = false; d->dropSlot = -1;
            if (GetCapture() == h) ReleaseCapture();
            if (wasDrag) for (auto& a : d->ia) a.slide = 0.f;  // slide offsets are baked into the new order
            if (pi >= 0 && pi < (int)g_items.size()) {
                if (wasDrag) {
                    if (remove && g_items[pi]->pinned) UnpinItem(*g_items[pi]);
                    else if (slot >= 0) ReorderItem(*d, pi, slot);
                } else {
                    OnItemClick(*d, pi, false);  // press and release within the drag threshold = a click on the pressed icon
                }
            }
            if (InHoverZone(*d, x, y, 6.f * d->ds)) HoverEnter(*d, x, y);
            else if (d->hovering) HoverEnd(*d, L"released outside the zone");
            else if (S.autoHide) SetTimer(h, T_HIDE, 600, nullptr);
            EnsureTimer(*d);
            return 0;
        }
        case WM_CAPTURECHANGED:
            if (d->dragging || d->pressIdx >= 0) { d->dragging = false; d->removeHint = false; d->pressIdx = -1; d->dropSlot = -1; EnsureTimer(*d); }
            return 0;
        case WM_MBUTTONUP: {
            int i = HitTest(*d, (float)GET_X_LPARAM(l), (float)GET_Y_LPARAM(l));
            if (i >= 0) OnItemClick(*d, i, true);
            return 0;
        }
        case WM_RBUTTONUP: {
            int i = HitTest(*d, (float)GET_X_LPARAM(l), (float)GET_Y_LPARAM(l));
            if (i >= 0 && g_items[i]->special) { OpenQuickLinkMenu(); return 0; }
            if (i >= 0) {
                POINT p{GET_X_LPARAM(l), GET_Y_LPARAM(l)};
                ClientToScreen(h, &p);
                ShowItemMenu(*d, i, p);
            }
            return 0;
        }
        case WM_MOUSEWHEEL: {
            POINT p{GET_X_LPARAM(l), GET_Y_LPARAM(l)};
            ScreenToClient(h, &p);
            int i = HitTest(*d, (float)p.x, (float)p.y);
            ULONGLONG now = GetTickCount64();
            if (i >= 0 && now - d->lastWheel > 150) {
                d->lastWheel = now;
                CycleWindows(*g_items[i], GET_WHEEL_DELTA_WPARAM(w) < 0);
            }
            return 0;
        }
        case WM_TIMER:
            if (w == T_HIDE) {
                KillTimer(h, T_HIDE);
                if (S.autoHide && !d->menuDepth && !d->dragging && !CursorOverDock(*d, (int)(8 * d->ds))) SetWantHidden(*d, true);
            } else if (w == T_WATCH) {
                if (HoverUpdate(*d)) EnsureTimer(*d);  // the cursor moved / left without a message reaching us
            } else if (w == T_REVEAL) {
                KillTimer(h, T_REVEAL);
                d->edgeArmed = false;
                POINT p;
                GetCursorPos(&p);
                RECT er{d->monRect.left, d->monRect.bottom - 4, d->monRect.right, d->monRect.bottom + 2};
                if (S.autoHide && d->wantHidden && PtInRect(&er, p)) {
                    SetWantHidden(*d, false);
                    SetTimer(h, T_HIDE, 1500, nullptr);  // hide again if the cursor never enters the Dock
                }
            }
            return 0;
        case WM_DESTROY:
            SetWindowLongPtrW(h, GWLP_USERDATA, 0);
            return 0;
    }
    return DefWindowProcW(h, m, w, l);
}
LRESULT CALLBACK BackdropProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (m == WM_ERASEBKGND) return 1;
    return DefWindowProcW(h, m, w, l);
}
LRESULT CALLBACK EdgeProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_NCCREATE) {
        SetWindowLongPtrW(h, GWLP_USERDATA, (LONG_PTR)((CREATESTRUCTW*)l)->lpCreateParams);
        return DefWindowProcW(h, m, w, l);
    }
    if (m == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (m == WM_ERASEBKGND) return 1;
    if (m == WM_MOUSEMOVE) {
        Dock* d = (Dock*)GetWindowLongPtrW(h, GWLP_USERDATA);
        if (d && d->hwnd && !d->edgeArmed && d->wantHidden) {
            d->edgeArmed = true;
            SetTimer(d->hwnd, T_REVEAL, 180, nullptr);  // small debounce so a fly-by of the cursor does not pop the Dock
        }
        return 0;
    }
    if (m == WM_NCDESTROY) SetWindowLongPtrW(h, GWLP_USERDATA, 0);
    return DefWindowProcW(h, m, w, l);
}


// ============================================================================
// 7. Actions: launch / activate / context menu / pin / reorder
// ============================================================================
void ForegroundNow(HWND h) {
    BringWindowToTop(h);
    SetForegroundWindow(h);
    HWND f = GetForegroundWindow();
    if (f != h && RootOf(f) != h) SwitchToThisWindow(h, TRUE);
}
// Minimise / restore is requested the way the real taskbar does it: WM_SYSCOMMAND to the window itself. The window's own process then
// runs the animation with the snapshot it took when the window was minimised (the snapshot cache is per process - restoring from here
// would find none and play a grey ghost). A short timer verifies the result and falls back to a direct call / re-asserts the focus.
struct PendAct { HWND h; ULONGLONG t0; bool minimize; bool forced; HWND prevFg; int tries; };
std::vector<PendAct> g_pend;
void ArmPend() {
    HWND c = g_ctrl.load();
    if (c) SetTimer(c, TC_ACT, 100, nullptr);
}
void MinimizeWindow(HWND h) {
    if (!IsWindow(h)) return;
    if (PostMessageW(h, WM_SYSCOMMAND, SC_MINIMIZE, 0)) {
        g_pend.push_back(PendAct{h, GetTickCount64(), true, false, nullptr, 0});
        ArmPend();
    } else ShowWindow(h, SW_MINIMIZE);
}
void ActivateWindow(HWND h) {
    if (!IsWindow(h)) return;
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid) AllowSetForegroundWindow(pid);
    if (IsIconic(h)) {
        if (PostMessageW(h, WM_SYSCOMMAND, SC_RESTORE, 0)) {
            g_pend.push_back(PendAct{h, GetTickCount64(), false, false, GetForegroundWindow(), 0});
            ArmPend();
            return;
        }
        ShowWindow(h, SW_RESTORE);
    }
    ForegroundNow(h);
}
void ProcessPending() {
    ULONGLONG now = GetTickCount64();
    for (size_t i = 0; i < g_pend.size();) {
        PendAct& p = g_pend[i];
        ULONGLONG age = now - p.t0;
        bool done = false;
        if (!IsWindow(p.h) || age > 3500) done = true;
        else if (p.minimize) {
            if (IsIconic(p.h) || !IsWindowVisible(p.h)) done = true;       // minimised (or the app hid itself to the tray)
            else if (age >= 700 && !p.forced) { p.forced = true; ShowWindow(p.h, SW_MINIMIZE); }
        } else if (IsIconic(p.h)) {
            if (age >= 450 && !p.forced) { p.forced = true; ShowWindow(p.h, SW_RESTORE); }
        } else {
            HWND fg = GetForegroundWindow();
            if (fg == p.h || RootOf(fg) == p.h) done = true;
            else if (age >= 100) {
                DWORD fpid = 0;
                if (fg) GetWindowThreadProcessId(fg, &fpid);
                // re-assert only while nothing else took the focus meanwhile (the previous window, the desktop or the shell still has it)
                if (!fg || fg == p.prevFg || fpid == GetCurrentProcessId()) { if (++p.tries <= 25) ForegroundNow(p.h); else done = true; }
                else done = true;
            }
        }
        if (done) g_pend.erase(g_pend.begin() + i); else ++i;
    }
    if (g_pend.empty()) { HWND c = g_ctrl.load(); if (c) KillTimer(c, TC_ACT); }
}
std::wstring LaunchTarget(const DockItem& it) {
    if (!it.lnk.empty() && PathFileExistsW(it.lnk.c_str())) return it.lnk;
    if (!it.aumid.empty()) return L"shell:AppsFolder\\" + it.aumid;
    return it.exe;
}
void StartBounceAnimations() {
    for (auto& dk : g_docks) EnsureTimer(*dk);
}
void LaunchItem(DockItem& it) {
    std::wstring file = LaunchTarget(it);
    if (file.empty()) return;
    SHELLEXECUTEINFOW sei{};
    sei.cbSize = sizeof(sei);
    sei.fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
    sei.lpFile = file.c_str();
    sei.nShow = SW_SHOWNORMAL;
    if (ShellExecuteExW(&sei)) {
        if (it.wins.empty()) { it.bounceStart = NowSec(); StartBounceAnimations(); }
    }
}
// Cycles through the windows of an app in a stable order (first-seen order, independent of z-order).
void CycleWindows(DockItem& it, bool forward) {
    std::vector<HWND> list;
    for (HWND w : it.stable) if (IsWindow(w)) list.push_back(w);
    if (list.empty()) return;
    HWND fgRoot = RootOf(GetForegroundWindow());
    int cur = -1;
    for (size_t i = 0; i < list.size(); ++i) if (list[i] == fgRoot) cur = (int)i;
    if (cur < 0 && it.cycleHwnd)
        for (size_t i = 0; i < list.size(); ++i) if (list[i] == it.cycleHwnd) cur = (int)i;
    int n = (int)list.size();
    int next;
    if (cur < 0) next = forward ? 0 : n - 1;
    else next = forward ? (cur + 1) % n : (cur + n - 1) % n;
    if (n == 1 && list[0] == fgRoot) return;
    it.cycleHwnd = list[next];
    ActivateWindow(list[next]);
}

// ---- Start button -------------------------------------------------------------------------------------
// The real Start menu is opened the way the keyboard does it (a tap of the Windows key): there is no stable public API for it.
void SendKeys(const WORD* seq, int n) {  // seq: virtual key codes, pressed in order and released in reverse order
    std::vector<INPUT> in;
    for (int k = 0; k < 2 * n; ++k) {
        INPUT i{};
        i.type = INPUT_KEYBOARD;
        i.ki.wVk = k < n ? seq[k] : seq[2 * n - 1 - k];
        i.ki.dwFlags = (k >= n ? KEYEVENTF_KEYUP : 0) | (i.ki.wVk == VK_LWIN ? KEYEVENTF_EXTENDEDKEY : 0);
        in.push_back(i);
    }
    SendInput((UINT)in.size(), in.data(), sizeof(INPUT));
}
void OpenStartMenu() { const WORD k[1] = {VK_LWIN}; SendKeys(k, 1); }
void OpenQuickLinkMenu() { const WORD k[2] = {VK_LWIN, 'X'}; SendKeys(k, 2); }  // right click on Start: the Win+X menu, like the real one
bool StartMenuIsForeground() {
    HWND fg = GetForegroundWindow();
    if (!fg) return false;
    DWORD pid = 0;
    GetWindowThreadProcessId(fg, &pid);
    if (!pid) return false;
    std::wstring n = FileNameOf(ExeOfPid(pid));
    return IEq(n, L"StartMenuExperienceHost.exe") || IEq(n, L"SearchHost.exe") || IEq(n, L"SearchApp.exe");
}

void OnItemClick(Dock& d, int idx, bool newInstance) {
    if (idx < 0 || idx >= (int)g_items.size()) return;
    ItemPtr keep = g_items[idx];
    DockItem& it = *keep;
    // the same icon clicked again within a moment (double click, nervous finger): one action only
    static std::wstring lastKey;
    static ULONGLONG lastTick = 0;
    ULONGLONG nowT = GetTickCount64();
    if (!newInstance && it.key == lastKey && nowT - lastTick < 280) return;
    lastKey = it.key; lastTick = nowT;
    if (it.special) {
        // The click that happens while the menu is open usually closes it already (it loses focus); opening it again then would make
        // the button useless as a toggle. pressStartOpen = the menu was open when the button went down.
        bool wasOpen = d.pressStartOpen;
        d.pressStartOpen = false;
        if (!newInstance && (!wasOpen || StartMenuIsForeground())) OpenStartMenu();
        return;
    }
    // windows that vanished since the last refresh must not turn a click into "launch another instance"
    std::vector<HWND> live;
    for (HWND w : it.wins) if (IsWindow(w)) live.push_back(w);
    if (newInstance || live.empty()) { LaunchItem(it); ScheduleRefresh(false); return; }
    HWND fg = RootOf(GetForegroundWindow());
    HWND activeWin = nullptr;
    for (HWND w : live) if (w == fg) activeWin = w;
    if (activeWin) {
        if (live.size() > 1) CycleWindows(it, true);
        else if (S.minimizeOnClick) MinimizeWindow(activeWin);
    } else if (it.active && live.size() > 1) {
        CycleWindows(it, true);  // a popup / dialog of this app is in front
    } else {
        ActivateWindow(live.front());
    }
}

// --- pin overlay -------------------------------------------------------------------
bool IsWinPin(const std::wstring& key) {
    for (auto& p : g_winPins) if (p.key == key) return true;
    return false;
}
void PinItem(DockItem& it) {
    auto hidden = LoadList(L"hiddenPins");
    auto f = std::find(hidden.begin(), hidden.end(), it.key);
    if (f != hidden.end()) {
        hidden.erase(f);
        SaveList(L"hiddenPins", hidden);
    } else if (!IsWinPin(it.key)) {
        auto extra = LoadList(L"extraPins");
        std::wstring line = it.key + L"\t" + (it.aumid.empty() ? L"p" : L"a") + L"\t" + (it.aumid.empty() ? it.exe : it.aumid) + L"\t" + it.name;
        extra.push_back(line);
        SaveList(L"extraPins", extra);
    }
    if (RefreshModel(true)) ModelChanged();
}
void UnpinItem(DockItem& it) {
    std::wstring key = it.key;
    if (IsWinPin(key)) {
        auto hidden = LoadList(L"hiddenPins");
        if (std::find(hidden.begin(), hidden.end(), key) == hidden.end()) hidden.push_back(key);
        SaveList(L"hiddenPins", hidden);
    }
    auto extra = LoadList(L"extraPins");
    extra.erase(std::remove_if(extra.begin(), extra.end(), [&](const std::wstring& l) { return l.compare(0, key.size() + 1, key + L"\t") == 0; }), extra.end());
    SaveList(L"extraPins", extra);
    if (RefreshModel(true)) ModelChanged();
}
void ReorderItem(Dock&, int from, int slot) {
    if (from < 0 || from >= (int)g_items.size() || slot < 0 || g_items[from]->special) return;
    std::vector<std::wstring> keys;
    std::wstring moved = g_items[from]->key;
    int skipped = 0;  // the Start button is not part of the saved order: slots counted with it are one too high
    for (size_t i = 0; i < g_items.size(); ++i) {
        if ((int)i == from) continue;
        if (g_items[i]->special) { if ((int)i < slot + 1) ++skipped; continue; }
        keys.push_back(g_items[i]->key);
    }
    slot = std::max(0, slot - skipped);
    slot = std::min<int>(slot, (int)keys.size());
    keys.insert(keys.begin() + slot, moved);
    // keep the remembered position of items that are not visible right now (closed apps, hidden pins)
    std::unordered_set<std::wstring> vis(keys.begin(), keys.end());
    for (auto& k : LoadList(L"order"))
        if (!k.empty() && k[0] != 1 && !vis.count(k)) { keys.push_back(k); vis.insert(k); }
    if (keys.size() > 200) keys.resize(200);
    SaveList(L"order", keys);
    g_lastSig.clear();
    if (RefreshModel(false)) ModelChanged();
}

// --- context menu ----------------------------------------------------------------------
Com<IShellItem> ShellItemFor(const DockItem& it) {
    Com<IShellItem> si;
    if (!it.aumid.empty() && SUCCEEDED(SHCreateItemFromParsingName((L"shell:AppsFolder\\" + it.aumid).c_str(), nullptr, IID_PPV_ARGS(si.put())))) return si;
    if (!it.exe.empty() && SUCCEEDED(SHCreateItemFromParsingName(it.exe.c_str(), nullptr, IID_PPV_ARGS(si.put())))) return si;
    si.reset();
    return si;
}
std::wstring VerbOf(IContextMenu* cm, UINT idCmd) {
    WCHAR w[128] = {};
    if (SUCCEEDED(cm->GetCommandString(idCmd, GCS_VERBW, nullptr, (LPSTR)w, ARRAYSIZE(w))) && w[0]) return w;
    char a[128] = {};
    if (SUCCEEDED(cm->GetCommandString(idCmd, GCS_VERBA, nullptr, a, sizeof(a))) && a[0]) {
        WCHAR u[128] = {};
        MultiByteToWideChar(CP_ACP, 0, a, -1, u, ARRAYSIZE(u));
        return u;
    }
    return L"";
}
// Only verbs that would destroy or move the application's files are removed; every other native verb stays.
void StripUnsafeVerbs(HMENU menu, IContextMenu* cm, UINT first) {
    static const wchar_t* bad[] = {L"delete", L"cut", L"rename", L"paste", L"pastelink", L"undelete"};
    for (int i = GetMenuItemCount(menu) - 1; i >= 0; --i) {
        UINT id = GetMenuItemID(menu, i);
        if (id == (UINT)-1 || id < first) continue;
        std::wstring verb = VerbOf(cm, id - first);
        if (verb.empty()) continue;
        for (auto b : bad) if (_wcsicmp(verb.c_str(), b) == 0) { DeleteMenu(menu, i, MF_BYPOSITION); break; }
    }
    // collapse separators
    for (int i = GetMenuItemCount(menu) - 1; i >= 0; --i) {
        MENUITEMINFOW mi{sizeof(mi), MIIM_FTYPE};
        GetMenuItemInfoW(menu, i, TRUE, &mi);
        if (!(mi.fType & MFT_SEPARATOR)) continue;
        bool prevSep = false;
        if (i + 1 < GetMenuItemCount(menu)) { MENUITEMINFOW m2{sizeof(m2), MIIM_FTYPE}; GetMenuItemInfoW(menu, i + 1, TRUE, &m2); prevSep = (m2.fType & MFT_SEPARATOR) != 0; }
        if (i == 0 || i == GetMenuItemCount(menu) - 1 || prevSep) DeleteMenu(menu, i, MF_BYPOSITION);
    }
}

struct RecentEntry { Com<IShellItem> item; Com<IShellLinkW> link; };
std::vector<RecentEntry> LoadRecent(const DockItem& it, HMENU menu, UINT baseId) {
    std::vector<RecentEntry> out;
    if (it.aumid.empty()) return out;
    Com<IApplicationDocumentLists> adl;
    if (FAILED(CoCreateInstance(CLSID_ApplicationDocumentLists, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(adl.put())))) return out;
    if (FAILED(adl->SetAppID(it.aumid.c_str()))) return out;
    Com<IObjectArray> arr;
    if (FAILED(adl->GetList(ADLT_RECENT, 8, IID_PPV_ARGS(arr.put())))) return out;
    UINT cnt = 0;
    arr->GetCount(&cnt);
    for (UINT i = 0; i < cnt && out.size() < 8; ++i) {
        RecentEntry e;
        std::wstring label;
        if (SUCCEEDED(arr->GetAt(i, IID_PPV_ARGS(e.item.put())))) {
            PWSTR nm = nullptr;
            if (SUCCEEDED(e.item->GetDisplayName(SIGDN_NORMALDISPLAY, &nm)) && nm) { label = nm; CoTaskMemFree(nm); }
        } else if (SUCCEEDED(arr->GetAt(i, IID_PPV_ARGS(e.link.put())))) {
            WCHAR t[260] = {};
            e.link->GetDescription(t, ARRAYSIZE(t));
            label = t;
        }
        if (label.empty()) continue;
        if (label.size() > 60) label = label.substr(0, 57) + L"...";
        AppendMenuW(menu, MF_STRING, baseId + (UINT)out.size(), label.c_str());
        out.push_back(std::move(e));
    }
    return out;
}
void OpenRecent(const RecentEntry& e) {
    std::wstring path, args;
    if (e.item) {
        PWSTR p = nullptr;
        if (SUCCEEDED(e.item->GetDisplayName(SIGDN_FILESYSPATH, &p)) && p) { path = p; CoTaskMemFree(p); }
        else if (SUCCEEDED(e.item->GetDisplayName(SIGDN_URL, &p)) && p) { path = p; CoTaskMemFree(p); }
    } else if (e.link) {
        WCHAR p[MAX_PATH * 2] = {}, a[2048] = {};
        e.link->GetPath(p, ARRAYSIZE(p), nullptr, 0);
        e.link->GetArguments(a, ARRAYSIZE(a));
        path = p; args = a;
    }
    if (path.empty()) return;
    ShellExecuteW(nullptr, L"open", path.c_str(), args.empty() ? nullptr : args.c_str(), nullptr, SW_SHOWNORMAL);
}

void ShowItemMenu(Dock& d, int idx, POINT) {
    if (idx < 0 || idx >= (int)g_items.size() || g_inMenu || g_items[idx]->special) return;
    ItemPtr keep = g_items[idx];
    DockItem& it = *keep;
    HMENU menu = CreatePopupMenu();
    if (!menu) return;
    enum { ID_OPEN = 1, ID_CLOSE, ID_PIN, ID_RECENT = 100, SHELL_FIRST = 1000 };
    AppendMenuW(menu, MF_STRING, ID_OPEN, it.wins.empty() ? L"Open" : L"New window");
    if (!it.wins.empty()) AppendMenuW(menu, MF_STRING, ID_CLOSE, it.wins.size() > 1 ? L"Close all windows" : L"Close window");
    AppendMenuW(menu, MF_STRING, ID_PIN, it.pinned ? L"Unpin from Dock" : L"Pin to Dock");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    int sepPos = GetMenuItemCount(menu) - 1;
    auto recent = LoadRecent(it, menu, ID_RECENT);
    if (recent.empty()) DeleteMenu(menu, sepPos, MF_BYPOSITION);
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    Com<IContextMenu> cm;
    Com<IShellItem> si = ShellItemFor(it);
    if (si && SUCCEEDED(si->BindToHandler(nullptr, BHID_SFUIObject, IID_PPV_ARGS(cm.put())))) {
        UINT flags = CMF_NORMAL | CMF_ITEMMENU;
        if (GetKeyState(VK_SHIFT) < 0) flags |= CMF_EXTENDEDVERBS;
        if (SUCCEEDED(cm->QueryContextMenu(menu, GetMenuItemCount(menu), SHELL_FIRST, 0x7FFF, flags))) {
            StripUnsafeVerbs(menu, cm.get(), SHELL_FIRST);
        } else cm.reset();
    }
    if (idx >= (int)d.rects.size()) { DestroyMenu(menu); return; }
    if (cm) {
        cm->QueryInterface(IID_PPV_ARGS(&g_cm2));
        cm->QueryInterface(IID_PPV_ARGS(&g_cm3));
    }
    const Rc& r = d.rects[idx];
    int x = d.winX + (int)(r.x + r.w * 0.5f), y = d.winY + (int)(d.plateT - 6 * d.ds);
    d.menuDepth++;
    g_inMenu = true;
    d.hot = -1;
    RenderDock(d);
    SetForegroundWindow(d.hwnd);
    UINT cmd = (UINT)TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_CENTERALIGN, x, y, d.hwnd, nullptr);
    PostMessageW(d.hwnd, WM_NULL, 0, 0);
    g_inMenu = false;
    d.menuDepth--;
    if (g_cm2) { g_cm2->Release(); g_cm2 = nullptr; }
    if (g_cm3) { g_cm3->Release(); g_cm3 = nullptr; }
    if (cmd == ID_OPEN) LaunchItem(it);
    else if (cmd == ID_CLOSE) { std::vector<HWND> ws = it.wins; for (HWND w : ws) PostMessageW(w, WM_CLOSE, 0, 0); }
    else if (cmd == ID_PIN) { if (it.pinned) UnpinItem(it); else PinItem(it); }
    else if (cmd >= ID_RECENT && cmd < ID_RECENT + 20) { if (cmd - ID_RECENT < recent.size()) OpenRecent(recent[cmd - ID_RECENT]); }
    else if (cmd >= SHELL_FIRST && cm) {
        CMINVOKECOMMANDINFOEX ici{};
        ici.cbSize = sizeof(ici);
        ici.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
        if (GetKeyState(VK_SHIFT) < 0) ici.fMask |= CMIC_MASK_SHIFT_DOWN;
        if (GetKeyState(VK_CONTROL) < 0) ici.fMask |= CMIC_MASK_CONTROL_DOWN;
        ici.hwnd = d.hwnd;
        ici.lpVerb = MAKEINTRESOURCEA(cmd - SHELL_FIRST);
        ici.lpVerbW = MAKEINTRESOURCEW(cmd - SHELL_FIRST);
        ici.nShow = SW_SHOWNORMAL;
        GetCursorPos(&ici.ptInvoke);
        cm->InvokeCommand((CMINVOKECOMMANDINFO*)&ici);
    }
    DestroyMenu(menu);
    HoverEnd(d, L"item menu closed");
    EnsureTimer(d);
    if (S.autoHide) SetTimer(d.hwnd, T_HIDE, 600, nullptr);
}


// ============================================================================
// 8. Controller: docks lifecycle, events, monitors, Explorer / settings changes
//    Everything below runs on the dock thread.
// ============================================================================
constexpr UINT WM_APP_QUIT = WM_APP + 1, WM_APP_SETTINGS = WM_APP + 2;
constexpr UINT_PTR TC_REFRESH = 1, TC_RECONCILE = 2, TC_WAIT = 3, TC_PINS = 4, TC_QUIT = 5, TC_FS = 6, TC_ICONS = 7, TC_LATE = 8, TC_GUARD = 9, TC_BEAT = 10;
UINT g_msgShellHook = 0, g_msgTaskbarCreated = 0;
bool g_refreshPending = false, g_pinsDirty = false, g_fsPending = false, g_reapplyAutoHide = false;
int g_waitTicks = 0;
HWINEVENTHOOK g_evFg = nullptr, g_evShow = nullptr, g_evCloak = nullptr, g_evLoc = nullptr;
DWORD g_locTid = 0;
HANDLE g_hFolder = INVALID_HANDLE_VALUE, g_hRegEvt = nullptr;
HKEY g_hRegKey = nullptr;
std::atomic<bool> g_quit{false};

void ModelChanged() {
    StructGuard sg;
    for (auto& dk : g_docks) ReflowDock(*dk);
    PruneIconCaches();
}
void ScheduleRefresh(bool pins) {
    HWND c = g_ctrl.load();
    if (!c) return;
    if (pins) g_pinsDirty = true;
    if (!g_refreshPending) { g_refreshPending = true; SetTimer(c, TC_REFRESH, 40, nullptr); }
}
void ScheduleReconcile() {
    HWND c = g_ctrl.load();
    if (c) SetTimer(c, TC_RECONCILE, 350, nullptr);
}

// Fullscreen detection needs to see size changes of the foreground window: one LOCATIONCHANGE hook that follows the foreground thread.
void CALLBACK WinEventProc(HWINEVENTHOOK, DWORD ev, HWND hwnd, LONG idObj, LONG idChild, DWORD, DWORD);
void RehookLocation(HWND fg) {
    DWORD pid = 0, tid = fg ? GetWindowThreadProcessId(fg, &pid) : 0;
    if (tid == g_locTid) return;
    if (g_evLoc) { UnhookWinEvent(g_evLoc); g_evLoc = nullptr; }
    g_locTid = tid;
    if (tid && pid != GetCurrentProcessId())
        g_evLoc = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, nullptr, WinEventProc, pid, tid, WINEVENT_OUTOFCONTEXT);
}

void CALLBACK WinEventProc(HWINEVENTHOOK, DWORD ev, HWND hwnd, LONG idObj, LONG idChild, DWORD, DWORD) {
    if (!g_active) return;
    HWND c = g_ctrl.load();
    if (ev == EVENT_SYSTEM_FOREGROUND) {
        if (UpdateActiveFlags()) RedrawAll();  // fast path: indicator follows focus immediately
        ScheduleRefresh(false);
        RehookLocation(hwnd);
        UpdateFullscreen();
        return;
    }
    if (idObj != OBJID_WINDOW || idChild != CHILDID_SELF || !hwnd) return;
    if (ev == EVENT_OBJECT_LOCATIONCHANGE) {
        if (c && !g_fsPending) { g_fsPending = true; SetTimer(c, TC_FS, 100, nullptr); }
        return;
    }
    if (GetAncestor(hwnd, GA_PARENT) != GetDesktopWindow()) return;
    if (ev == EVENT_OBJECT_SHOW) {  // a taskbar window (re)appeared: clip it again / create docks for new monitors
        std::wstring cls = ClassOf(hwnd);
        if (cls == L"Shell_TrayWnd" || cls == L"Shell_SecondaryTrayWnd") { ScheduleReconcile(); return; }
    }
    LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if ((ex & WS_EX_TOOLWINDOW) && !(ex & WS_EX_APPWINDOW)) return;
    ScheduleRefresh(false);
}

struct MonInfo { HMONITOR m; RECT r; bool primary; };
BOOL CALLBACK EnumMonProc(HMONITOR m, HDC, LPRECT, LPARAM lp) {
    MONITORINFO mi{sizeof(mi)};
    if (GetMonitorInfoW(m, &mi)) ((std::vector<MonInfo>*)lp)->push_back({m, mi.rcMonitor, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0});
    return TRUE;
}

std::unique_ptr<Dock> CreateDock(HMONITOR mon) {
    auto d = std::make_unique<Dock>();
    d->mon = mon;
    d->hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, L"WhDockWnd", L"Dock", WS_POPUP, 0, 0, 1, 1,
                              nullptr, nullptr, g_hInst, d.get());
    if (!d->hwnd) return nullptr;
    d->backdrop = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, L"WhDockBackdrop", L"", WS_POPUP, 0, 0, 1, 1,
                                  nullptr, nullptr, g_hInst, nullptr);
    if (d->backdrop) ApplyAccent(d->backdrop);
    d->edge = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, L"WhDockHot", L"", WS_POPUP, 0, 0, 1, 1,
                              nullptr, nullptr, g_hInst, d.get());
    if (d->edge) SetLayeredWindowAttributes(d->edge, 0, 1, LWA_ALPHA);  // alpha 1/255: invisible but still hit-testable
    d->wantHidden = S.autoHide;
    d->hideAmt = S.autoHide ? 1.f : 0.f;
    return d;
}
void DestroyDock(Dock& d) {
    d.timerOn = false;
    if (d.hwnd) { KillTimer(d.hwnd, T_HIDE); KillTimer(d.hwnd, T_REVEAL); KillTimer(d.hwnd, T_WATCH); }
    if (d.abOn && d.hwnd) {
        APPBARDATA abd{};
        abd.cbSize = sizeof(abd);
        abd.hWnd = d.hwnd;
        SHAppBarMessage(ABM_REMOVE, &abd);
        d.abOn = false;
    }
    d.ia.clear(); d.iaSpare.clear(); d.ghosts.clear(); d.brush.reset(); d.rt.reset(); d.tf.reset(); d.ellipsis.reset(); d.lblLayout.reset();
    if (d.edge) { SetWindowLongPtrW(d.edge, GWLP_USERDATA, 0); DestroyWindow(d.edge); d.edge = nullptr; }
    if (d.hwnd) { DestroyWindow(d.hwnd); d.hwnd = nullptr; }
    if (d.backdrop) { DestroyWindow(d.backdrop); d.backdrop = nullptr; }
    if (d.memDC) {
        if (d.dib) { SelectObject(d.memDC, d.oldBmp); DeleteObject(d.dib); d.dib = nullptr; }
        DeleteDC(d.memDC);
        d.memDC = nullptr;
    }
}

// Create / remove docks to match the monitor mode; clip the original taskbars; reflow everything.
void ReconcileDocksImpl() {
    StructGuard sg;
    std::vector<MonInfo> mons;
    EnumDisplayMonitors(nullptr, nullptr, EnumMonProc, (LPARAM)&mons);
    std::vector<HWND> trays = FindOwnTaskbars();
    std::unordered_set<HMONITOR> wanted;
    for (auto& m : mons) {
        if (S.monitorMode == 2 || m.primary) wanted.insert(m.m);
    }
    if (S.monitorMode == 1)
        for (HWND t : trays) wanted.insert(MonitorFromWindow(t, MONITOR_DEFAULTTONEAREST));
    if (wanted.empty() && !mons.empty()) wanted.insert(mons[0].m);

    for (size_t i = 0; i < g_docks.size();) {
        if (!wanted.count(g_docks[i]->mon)) { DestroyDock(*g_docks[i]); g_docks.erase(g_docks.begin() + i); }
        else ++i;
    }
    for (HMONITOR m : wanted) {
        bool have = false;
        for (auto& dk : g_docks) if (dk->mon == m) have = true;
        if (!have) { auto d = CreateDock(m); if (d) g_docks.push_back(std::move(d)); }
    }
    for (auto& dk : g_docks) {
        for (auto& m : mons) if (m.m == dk->mon) dk->monRect = m.r;
        ReflowDock(*dk, false);
    }
    // original taskbar(s)
    PurgeDeadSlots();
    std::vector<HWND> clipped(g_clipped.begin(), g_clipped.end());
    if (S.taskbarMode == 0) {
        for (HWND h : clipped) UnclipTaskbar(h);
    } else {
        // every taskbar of this process is hidden - also the ones on monitors that have no Dock (secondary taskbars)
        std::unordered_set<HWND> want(trays.begin(), trays.end());
        for (HWND h : clipped) if (!want.count(h)) UnclipTaskbar(h);
        for (HWND t : want) ClipTaskbar(t);
    }
    PruneIconCaches();
    UpdateFullscreen();
}
// Cheap watchdog (every 3 s, no enumeration unless something is wrong): every clipped taskbar must still have an empty
// region, the primary taskbar of this process must be one of them.
void GuardTaskbars() {
    if (S.taskbarMode == 0 || g_busy) return;
    bool redo = false;
    for (HWND h : std::vector<HWND>(g_clipped.begin(), g_clipped.end())) {
        if (!IsWindow(h)) { redo = true; continue; }
        RECT rb{};
        if (GetWindowRgnBox(h, &rb) != NULLREGION) { ClipTaskbar(h); }
    }
    HWND primary = FindWindowW(L"Shell_TrayWnd", nullptr);
    DWORD pid = 0;
    if (primary) GetWindowThreadProcessId(primary, &pid);
    if (primary && pid == GetCurrentProcessId() && !g_clipped.count(primary)) redo = true;
    if (redo) ScheduleReconcile();
}
void ReconcileDocks() {
    if (!g_active) return;
    if (g_busy) { g_deferReconcile = true; return; }  // a window procedure of ours is running (menu / message pump)
    ReconcileDocksImpl();
}

void RearmWatchers() {
    if (g_hFolder != INVALID_HANDLE_VALUE) { FindCloseChangeNotification(g_hFolder); g_hFolder = INVALID_HANDLE_VALUE; }
    std::wstring dir = PinnedFolder();
    if (!dir.empty()) g_hFolder = FindFirstChangeNotificationW(dir.c_str(), FALSE, FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE);
    if (!g_hRegEvt) g_hRegEvt = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!g_hRegKey) RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Taskband", 0, KEY_NOTIFY | KEY_READ, &g_hRegKey);
    if (g_hRegKey && g_hRegEvt) RegNotifyChangeKeyValue(g_hRegKey, FALSE, REG_NOTIFY_CHANGE_LAST_SET, g_hRegEvt, TRUE);
}

bool Activate() {
    if (g_active) return true;
    HWND c = g_ctrl.load();
    if (!c || !InitFactories()) return false;
    g_active = true;
    SetWinTaskbarAutoHide(S.autoHideWinTaskbar);
    RegisterShellHookWindow(c);
    g_evFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    g_evShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, nullptr, WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    g_evCloak = SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, nullptr, WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    RefreshModel(true);
    g_hookActive = true;
    ReconcileDocksImpl();
    ModelChanged();
    RearmWatchers();
    RehookLocation(GetForegroundWindow());
    SetTimer(c, TC_ICONS, 8000, nullptr);
    SetTimer(c, TC_GUARD, 3000, nullptr);
    SetTimer(c, TC_BEAT, 1000, nullptr);
    return true;
}
void Deactivate() {
    if (!g_active) return;
    g_active = false;
    g_hookActive = false;
    HWND c = g_ctrl.load();
    if (c) {
        KillTimer(c, TC_REFRESH); KillTimer(c, TC_RECONCILE); KillTimer(c, TC_PINS); KillTimer(c, TC_LATE);
        KillTimer(c, TC_FS); KillTimer(c, TC_ICONS); KillTimer(c, TC_GUARD); KillTimer(c, TC_BEAT); KillTimer(c, TC_ACT); KillTimer(c, TC_RETRYRF);
        DeregisterShellHookWindow(c);
    }
    if (g_evFg) UnhookWinEvent(g_evFg);
    if (g_evShow) UnhookWinEvent(g_evShow);
    if (g_evCloak) UnhookWinEvent(g_evCloak);
    if (g_evLoc) UnhookWinEvent(g_evLoc);
    g_evFg = g_evShow = g_evCloak = g_evLoc = nullptr;
    g_locTid = 0;
    {
        StructGuard sg;
        for (auto& dk : g_docks) DestroyDock(*dk);
        g_docks.clear();
    }
    g_deferReconcile = g_deferSettings = false;
    g_pend.clear();
    UnclipAllSync();
    SetWinTaskbarAutoHide(false);
    g_items.clear(); g_winPins.clear(); g_winCache.clear(); g_winSeq.clear(); ++g_iconEpoch; g_iconCache.clear(); g_icoPending.clear(); ResetPack(); g_nameCache.clear(); g_seen.clear(); g_lastSig.clear();
    if (g_hFolder != INVALID_HANDLE_VALUE) { FindCloseChangeNotification(g_hFolder); g_hFolder = INVALID_HANDLE_VALUE; }
    if (g_hRegKey) { RegCloseKey(g_hRegKey); g_hRegKey = nullptr; }
    if (g_hRegEvt) { CloseHandle(g_hRegEvt); g_hRegEvt = nullptr; }
    g_d2d.reset(); g_wic.reset(); g_dw.reset();
}

void ApplySettingsChange() {
    if (g_busy) { g_deferSettings = true; return; }
    LoadSettings();
    if (!g_active) return;
    StructGuard sg;
    g_deferReconcile = false;
    ResetIconCaches();
    ResetPack();  // rescan the icon pack folder
    SetWinTaskbarAutoHide(S.autoHideWinTaskbar);
    for (auto& dk : g_docks) {
        dk->ia.clear(); dk->ghosts.clear();  // fresh scales / slides (SyncAnim recreates them)
        dk->env = 0; dk->sx = dk->su = dk->cx; dk->zoom = 0; dk->magPrev = false; dk->hoverOutSince = 0;
        dk->lblKey.clear(); dk->lblHotKey.clear(); dk->lblAlpha = 0; dk->lblLayout.reset(); dk->lblLayoutText.clear();
        dk->wantHidden = S.autoHide;
        dk->hideAmt = S.autoHide ? 1.f : 0.f;
        KillTimer(dk->hwnd, T_HIDE);
        if (dk->hovering) SetTimer(dk->hwnd, T_WATCH, 120, nullptr);
        if (dk->backdrop) ApplyAccent(dk->backdrop);
    }
    g_lastSig.clear();
    RefreshModel(true);
    ReconcileDocksImpl();
    ModelChanged();
}
void FlushDeferred() {
    if (g_busy || g_structural) return;
    if (!g_active) { g_deferReconcile = g_deferSettings = false; return; }
    if (g_deferSettings) { g_deferSettings = false; ApplySettingsChange(); }
    if (g_deferReconcile) { g_deferReconcile = false; ReconcileDocksImpl(); }
}

LRESULT CALLBACK CtrlWndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == g_msgShellHook && g_msgShellHook) {
        switch (w & 0x7FFF) {
            case HSHELL_WINDOWCREATED: case HSHELL_WINDOWDESTROYED: case HSHELL_WINDOWACTIVATED: ScheduleRefresh(false); break;
            case 6 /*HSHELL_REDRAW: title / icon changed*/: if (g_pendSeen.count((HWND)l)) ScheduleRefresh(false); break;
        }
        return 0;
    }
    if (m == g_msgTaskbarCreated && g_msgTaskbarCreated) {
        // Do not talk to the taskbar from inside this broadcast: just re-apply everything shortly afterwards.
        if (!g_active) { if (!FindOwnTaskbars().empty()) Activate(); }
        else { g_reapplyAutoHide = true; ScheduleReconcile(); }
        return 0;
    }
    switch (m) {
        case WM_APP_QUIT:
            if (g_busy) { EndMenu(); SetTimer(h, TC_QUIT, 30, nullptr); return 0; }
            PostQuitMessage(0);
            return 0;
        case WM_APP_SETTINGS: ApplySettingsChange(); return 0;
        case WM_APP_FRAME: OnFrame(); return 0;
        case WM_APP_ICONS: if (g_active) DrainIconResults(); else g_icoNotify = false; return 0;
        case WM_DISPLAYCHANGE:
            if (g_active) {
                ScheduleReconcile();
                SetTimer(h, TC_LATE, 2500, nullptr);  // taskbars are recreated a bit later on monitor changes
            }
            return 0;
        case WM_TIMER:
            if (w == TC_REFRESH) {
                KillTimer(h, TC_REFRESH);
                g_refreshPending = false;
                bool pins = g_pinsDirty;
                g_pinsDirty = false;
                g_needRetry = false;
                if (g_active && RefreshModel(pins)) ModelChanged();
                // windows that are not ready yet (cloaked splash, no title, host process not attributed) are looked at again a few times
                if (g_active && g_needRetry && g_retryLeft > 0) SetTimer(h, TC_RETRYRF, 350, nullptr);
            } else if (w == TC_RECONCILE || w == TC_LATE) {
                KillTimer(h, w);
                if (g_active) {
                    if (g_reapplyAutoHide && !g_busy) { g_reapplyAutoHide = false; SetWinTaskbarAutoHide(S.autoHideWinTaskbar); }
                    ReconcileDocks();
                }
            } else if (w == TC_PINS) {
                KillTimer(h, TC_PINS);
                if (g_active) ScheduleRefresh(true);
            } else if (w == TC_FS) {
                KillTimer(h, TC_FS);
                g_fsPending = false;
                if (g_active) UpdateFullscreen();
            } else if (w == TC_ICONS) {  // slow housekeeping: retry placeholder icons, re-arm lost change watchers
                if (g_active) {
                    if (S.useIconPack && g_packBuilt && g_packSig != PackSigDir(PackFolder(), 0)) {  // PNG files added / replaced / removed: reload by itself
                        StructGuard sg;
                        ResetIconCaches();
                        ResetPack();
                        ModelChanged();
                    }
                    if (g_hasGenericIcons) { g_hasGenericIcons = false; for (auto& dk : g_docks) PrefetchIcons(*dk); }  // re-queue placeholders (async)
                    if (g_hFolder == INVALID_HANDLE_VALUE || !g_hRegKey) RearmWatchers();
                }
            } else if (w == TC_RETRY) {
                KillTimer(h, TC_RETRY);
                if (g_active) RedrawAll();
            } else if (w == TC_RETRYRF) {
                KillTimer(h, TC_RETRYRF);
                if (g_active && g_retryLeft > 0) { --g_retryLeft; ScheduleRefresh(false); }
            } else if (w == TC_ACT) {
                if (g_active) ProcessPending(); else KillTimer(h, TC_ACT);
            } else if (w == TC_BEAT) {  // safety net: the window list is re-checked every 3 s even if a notification was lost
                static int beats = 0;
                if (g_active && ++beats % 3 == 0) ScheduleRefresh(false);
            } else if (w == TC_GUARD) {  // someone else (another mod / Explorer) replaced a taskbar region, or a taskbar appeared unnoticed
                if (g_active) GuardTaskbars();
            } else if (w == TC_QUIT) {
                if (g_busy) EndMenu();
                else { KillTimer(h, TC_QUIT); PostQuitMessage(0); }
            } else if (w == TC_WAIT) {  // waiting for this process to own the taskbar (bounded: ~10 minutes)
                if (g_active || ++g_waitTicks > 300) { KillTimer(h, TC_WAIT); return 0; }
                if (!FindOwnTaskbars().empty()) { if (Activate()) KillTimer(h, TC_WAIT); }
            }
            return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

DWORD WINAPI DockThread(LPVOID) {
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    if (FAILED(OleInitialize(nullptr))) return 1;
    g_msgShellHook = RegisterWindowMessageW(L"SHELLHOOK");
    g_msgTaskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");

    WNDCLASSEXW wc{sizeof(wc)};
    wc.hInstance = g_hInst;
    wc.lpfnWndProc = CtrlWndProc;
    wc.lpszClassName = L"WhDockCtrl";
    RegisterClassExW(&wc);
    WNDCLASSEXW wd{sizeof(wd)};
    wd.hInstance = g_hInst;
    wd.lpfnWndProc = DockWndProc;
    wd.lpszClassName = L"WhDockWnd";
    wd.style = CS_DBLCLKS;
    wd.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassExW(&wd);
    WNDCLASSEXW wb{sizeof(wb)};
    wb.hInstance = g_hInst;
    wb.lpfnWndProc = BackdropProc;
    wb.lpszClassName = L"WhDockBackdrop";
    RegisterClassExW(&wb);
    WNDCLASSEXW we{sizeof(we)};
    we.hInstance = g_hInst;
    we.lpfnWndProc = EdgeProc;
    we.lpszClassName = L"WhDockHot";
    we.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassExW(&we);

    HWND ctrl = CreateWindowExW(WS_EX_TOOLWINDOW, L"WhDockCtrl", L"", WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, g_hInst, nullptr);
    g_ctrl = ctrl;
    if (ctrl && !g_quit.load()) {
        StartIconWorker();
        StartPacer();
        if (!FindOwnTaskbars().empty()) Activate();
        if (!g_active) SetTimer(ctrl, TC_WAIT, 2000, nullptr);
        for (;;) {
            HANDLE hs[2];
            DWORD cnt = 0;
            HANDLE hF = (g_active && g_hFolder != INVALID_HANDLE_VALUE) ? g_hFolder : nullptr;
            HANDLE hR = (g_active && g_hRegEvt) ? g_hRegEvt : nullptr;
            if (hF) hs[cnt++] = hF;
            if (hR) hs[cnt++] = hR;
            DWORD r = MsgWaitForMultipleObjectsEx(cnt, cnt ? hs : nullptr, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r < WAIT_OBJECT_0 + cnt) {
                HANDLE sig = hs[r - WAIT_OBJECT_0];
                if (sig == hF) FindNextChangeNotification(g_hFolder);
                else if (sig == hR && g_hRegKey) RegNotifyChangeKeyValue(g_hRegKey, FALSE, REG_NOTIFY_CHANGE_LAST_SET, g_hRegEvt, TRUE);
                SetTimer(ctrl, TC_PINS, 300, nullptr);
            }
            MSG msg;
            bool quit = false;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) { quit = true; break; }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            if (quit || g_quit.load()) break;
        }
    }
    Deactivate();
    StopPacer();
    StopIconWorker();
    g_ctrl = nullptr;
    if (ctrl) DestroyWindow(ctrl);
    UnregisterClassW(L"WhDockCtrl", g_hInst);
    UnregisterClassW(L"WhDockWnd", g_hInst);
    UnregisterClassW(L"WhDockBackdrop", g_hInst);
    UnregisterClassW(L"WhDockHot", g_hInst);
    OleUninitialize();
    return 0;
}

HANDLE g_thread = nullptr;

std::atomic<bool> g_unloading{false};
bool g_isShell = false;  // this process is the shell explorer.exe (Dock + taskbar clipping live only here)

// The shell explorer.exe: %WINDIR%\explorer.exe started without arguments (folder windows of "separate process" mode and COM servers get
// arguments such as -Embedding / /factory and are NOT the shell).
bool IsShellProcess() {
    WCHAR p[MAX_PATH] = {}, win[MAX_PATH] = {};
    if (!GetModuleFileNameW(nullptr, p, MAX_PATH) || !GetWindowsDirectoryW(win, MAX_PATH)) return false;
    if (!IEq(p, std::wstring(win) + L"\\explorer.exe")) return false;
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv) LocalFree(argv);
    return argc <= 1;
}

}  // namespace

// ============================================================================
// 9. Windhawk entry points
// ============================================================================
BOOL Wh_ModInit() {
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&Wh_ModInit, &g_hInst);
    g_unloading = false;
    g_isShell = IsShellProcess();
    if (!g_isShell) return TRUE;  // only the shell explorer.exe owns the Dock and the taskbar clipping

    InitializeCriticalSection(&g_rgnCs);  // intentionally never deleted: the SetWindowRgn hook may still be running while we unload
    g_rgnCsInit = true;
    g_msgClip = RegisterWindowMessageW(L"WhFloatingDock_ClipTaskbar");
    LoadSettings();
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    void* target = user32 ? (void*)GetProcAddress(user32, "SetWindowRgn") : nullptr;
    if (target) Wh_SetFunctionHook(target, (void*)SetWindowRgn_Hook, (void**)&SetWindowRgn_Original);
    g_quit = false;
    g_thread = CreateThread(nullptr, 0, DockThread, nullptr, 0, nullptr);
    return g_thread != nullptr;
}

void Wh_ModBeforeUninit() {
    g_unloading.store(true, std::memory_order_relaxed);
    g_hookActive = false;
}

void Wh_ModUninit() {
    g_unloading.store(true, std::memory_order_relaxed);
    if (!g_isShell) return;
    g_quit = true;
    g_hookActive = false;
    if (g_thread) {
        // The thread owns every window / hook it created and tears them down itself. The quit message is re-posted
        // until the thread is gone (it may not have created its controller window yet).
        bool done = false;
        for (int i = 0; i < 400 && !done; ++i) {
            HWND c = g_ctrl.load();
            if (c) PostMessageW(c, WM_APP_QUIT, 0, 0);
            done = WaitForSingleObject(g_thread, 50) == WAIT_OBJECT_0;
        }
        if (!done) Wh_Log(L"Dock thread did not stop in time");
        CloseHandle(g_thread);
        g_thread = nullptr;
    }
}

void Wh_ModSettingsChanged() {
    if (!g_isShell) return;
    HWND c = g_ctrl.load();
    if (c) PostMessageW(c, WM_APP_SETTINGS, 0, 0);
}
