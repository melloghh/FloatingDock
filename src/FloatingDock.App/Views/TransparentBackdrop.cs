using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Views;

/// <summary>
/// A fully transparent window backdrop: with it the XAML content is composited straight onto the desktop, which is what
/// lets the dock look like a floating pill. (Windows App SDK 1.8 ships no ready-made "transparent" backdrop.)
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private global::Windows.UI.Composition.Compositor? _compositor;
    private global::Windows.UI.Composition.CompositionColorBrush? _brush;
    private AlphaWindow? _alpha;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId);
        _alpha = new AlphaWindow(hwnd);
        _compositor = new global::Windows.UI.Composition.Compositor();
        _brush = _compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
        base.OnTargetConnected(connectedTarget, xamlRoot);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        _alpha?.Dispose(); _alpha = null;
        _brush?.Dispose(); _brush = null;
        _compositor?.Dispose(); _compositor = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
