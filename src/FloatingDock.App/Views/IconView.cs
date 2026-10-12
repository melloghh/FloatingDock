using FloatingDock.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Views;

/// <summary>The picture of one app (dock slot or Launchpad tile): drawn glyph first, real icon as soon as it is decoded.</summary>
internal sealed class IconView : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private UIElement? _fallback;
    private AppRef? _app;
    private GlyphKind _kind;
    private int _px;
    private int _version;

    public IconView()
    {
        Children.Add(_image);
        IsHitTestVisible = false;
    }

    public AppRef? App => _app;

    public void Bind(AppRef app, GlyphKind kind, int decodePx)
    {
        bool same = _app is not null && _app.Key == app.Key && _kind == kind && _px == decodePx;
        _app = app; _kind = kind; _px = decodePx;
        // An unchanged binding may still be loading, or legitimately use a glyph.
        // Only ReloadIcons explicitly retries it; rebuilding running-app state must not start another decode.
        if (same) return;
        if (!same) _image.Source = null;
        Reload();
    }

    /// <summary>Re-resolves the icon (after an override / pack change). Keeps the old picture until the new one is ready.</summary>
    public async void Reload()
    {
        if (_app is null) return;
        int v = ++_version;
        if (_image.Source is null) ShowFallback();
        var bmp = await DockApp.Current.Icons.GetAsync(_app, _px, systemFallback: _kind == GlyphKind.App);
        if (v != _version) return;
        if (bmp is not null) { _image.Source = bmp; HideFallback(); }
        else { _image.Source = null; ShowFallback(); }
    }

    private void ShowFallback()
    {
        if (_fallback is not null) Children.Remove(_fallback);
        _fallback = Glyphs.Create(_kind, _app?.Name);
        Children.Insert(0, _fallback);
    }

    private void HideFallback()
    {
        if (_fallback is null) return;
        Children.Remove(_fallback);
        _fallback = null;
    }
}
