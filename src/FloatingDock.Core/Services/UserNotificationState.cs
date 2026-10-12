namespace FloatingDock.Core.Services;

/// <summary>QUERY_USER_NOTIFICATION_STATE (shellapi.h).</summary>
public enum UserNotificationState
{
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

public static class UserNotificationPolicy
{
    /// <summary>
    /// Interprets the result of SHQueryUserNotificationState. A failed HRESULT or an unknown value means "do not know" and is never treated as fullscreen,
    /// so a failing API cannot make the dock disappear.
    /// </summary>
    public static bool IsFullscreen(int hresult, int state)
    {
        if (hresult < 0) return false;                      // FAILED(hr)
        return state is (int)UserNotificationState.RunningD3DFullScreen or (int)UserNotificationState.PresentationMode;
    }
}
