using FloatingDock.Core.Configuration;

namespace FloatingDock.Core.Services;

/// <summary>Decides how the dock shares the screen with maximized and full-screen windows.</summary>
public static class DockSpacePolicy
{
    /// <summary>The dock strip is reserved when asked for explicitly or when full-screen windows must stay above the dock.</summary>
    public static bool ShouldReserve(DockSettings s, bool userHidden) =>
        (s.ReserveWorkArea || s.KeepDockInFullscreen) && s.TaskbarMode != TaskbarMode.Leave && !s.AutoHide && !userHidden;

    /// <summary>Whether a covering window hides the dock. In "keep" mode only Windows-reported full-screen games and presentations still do.</summary>
    public static bool HidesForCoveringWindow(DockSettings s, bool windowsReportsFullscreen) =>
        s.HideOnFullscreen && (!s.KeepDockInFullscreen || windowsReportsFullscreen);

    /// <summary>Full-screen windows are fitted above the dock only in "keep" mode and never for Windows-reported full-screen games.</summary>
    public static bool ShouldFitFullscreen(DockSettings s, bool windowsReportsFullscreen) =>
        s.KeepDockInFullscreen && ShouldReserve(s, false) && !windowsReportsFullscreen;
}
