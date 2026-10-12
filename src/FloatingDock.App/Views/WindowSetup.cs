using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace FloatingDock.UI.Views;

internal static class WindowSetup
{
    public static IntPtr Hwnd(Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    /// <summary>No title bar, no frame, not resizable, not listed in Alt+Tab.</summary>
    public static void MakeBorderless(Window w)
    {
        w.ExtendsContentIntoTitleBar = true;
        var ap = w.AppWindow;
        if (ap.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        ap.IsShownInSwitchers = false;
    }

    public static void ApplyTheme(FrameworkElement root, FloatingDock.Core.Configuration.ThemeMode mode) =>
        root.RequestedTheme = mode switch
        {
            FloatingDock.Core.Configuration.ThemeMode.Light => ElementTheme.Light,
            FloatingDock.Core.Configuration.ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    /// <summary>Lets the user choose a PNG / ICO and stores it as the icon of the app (override beats icon pack and system icon).</summary>
    public static async Task PickIconAsync(IntPtr ownerHwnd, FloatingDock.Core.Models.AppRef app)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".ico");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var bytes = await Task.Run(() => Services.IconService.ReadLimited(file.Path))
                ?? throw new InvalidDataException("The file is not a valid PNG or ICO within the icon limits.");
            try { await Services.IconService.DecodeAsync(bytes, 128); }
            catch (Exception ex) { throw new InvalidDataException("The image could not be decoded. The previous icon was kept.", ex); }
            DockApp.Current.Overrides.Set(app.Key, file.Path, bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            DockApp.Current.Notify("Icon not changed", ex.Message, true);          // the previous icon is kept
        }
        catch (Exception ex) { DockApp.Current.Log.Warn("icon pick failed: " + ex.Message); }
    }
}
