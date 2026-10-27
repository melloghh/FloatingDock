using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Launchpad;

/// <summary>One app in the Launchpad grid: icon + name. Instances are pooled by <see cref="TileFactory"/>.</summary>
internal sealed class LaunchpadTile : Grid
{
    private readonly IconView _icon = new();
    private readonly TextBlock _name = new()
    {
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 12.5, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center,
    };
    private static readonly Brush Idle = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush Hover = new SolidColorBrush(global::Windows.UI.Color.FromArgb(40, 255, 255, 255));
    private readonly Border _highlight=new() { Background=Hover,CornerRadius=new CornerRadius(14),IsHitTestVisible=false,Margin=new Thickness(-6,-10,-6,-6) };

    public AppRef? App { get; private set; }
    private int _iconSize, _iconEpoch=-1;
    private double _width,_scale;
    internal static int IconEpoch { get; set; }
    internal static int BindingChanges { get; private set; }

    public LaunchpadTile()
    {
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(this,true);
        CornerRadius = new CornerRadius(14);
        Background = Idle;
        Padding = new Thickness(6, 10, 6, 6);
        Children.Add(_highlight);
        SurfaceMotion.Reset(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_highlight),0);
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(_icon);
        stack.Children.Add(_name);
        Children.Add(stack);
        PointerEntered += (_,_)=>SetHovered(true);
        PointerExited += (_,_)=>SetHovered(false);
    }
    internal void SetHovered(bool hovered)
    {
        // The tile and its rounded highlight keep their layout bounds. Only the icon moves.
        var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_icon);
        visual.CenterPoint=new System.Numerics.Vector3((float)_icon.ActualWidth/2,(float)_icon.ActualHeight/2,0);
        SurfaceMotion.Animate(visual,1,hovered?1.035f:1,180);
        SurfaceMotion.Animate(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_highlight),hovered?1:0,1,140);
    }
    internal Border HighlightForDiagnostics=>_highlight;

    public void Bind(AppRef app, int iconSize, double width, double scale)
    {
        bool same=App?.Key==app.Key&&_iconSize==iconSize&&_width==width&&_scale==scale;
        bool iconsChanged=_iconEpoch!=IconEpoch;
        App=app;
        if(same&&!iconsChanged) { if(_name.Text!=app.Name)_name.Text=app.Name;return; }
        _iconSize=iconSize;_width=width;_scale=scale;_iconEpoch=IconEpoch;BindingChanges++;
        App = app;
        _icon.Width = _icon.Height = iconSize;
        _name.Width = Math.Max(40, width - 14);
        _name.Text = app.Name;
        _icon.Bind(app, GlyphKind.App, (int)Math.Ceiling(iconSize * scale));
        if(same&&iconsChanged)_icon.Reload();
    }

    public void ReloadIcon() { _iconEpoch=IconEpoch;_icon.Reload(); }
    internal void ResetMotion()
    {
        Background=Idle;
        SurfaceMotion.Reset(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this));
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(this,true);
        var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this);visual.StopAnimation("Translation");visual.Properties.InsertVector3("Translation",System.Numerics.Vector3.Zero);
        SurfaceMotion.Reset(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_icon));
        SurfaceMotion.Reset(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_highlight),0);
    }
}

internal sealed class TileFactory : IElementFactory
{
    private readonly Stack<LaunchpadTile> _pool = new();
    private readonly Func<(int Icon, double Width, double Scale)> _metrics;
    private readonly Action<LaunchpadTile> _onCreate;

    public TileFactory(Func<(int Icon, double Width, double Scale)> metrics, Action<LaunchpadTile> onCreate)
    {
        _metrics = metrics;
        _onCreate = onCreate;
    }

    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        LaunchpadTile tile;
        if (_pool.Count > 0) tile = _pool.Pop();
        else { tile = new LaunchpadTile(); _onCreate(tile); }
        var m = _metrics();
        tile.Width = m.Width;
        tile.Bind((AppRef)args.Data, m.Icon, m.Width, m.Scale);
        return tile;
    }

    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        if (args.Element is LaunchpadTile t) { t.ResetMotion();_pool.Push(t); }
    }
}
