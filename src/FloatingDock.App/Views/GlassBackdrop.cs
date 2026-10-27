using FloatingDock.Windows.Native;
using Microsoft.Graphics.Canvas.Effects;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Views;

/// <summary>Own-window blur with a composited tint, independent of Windows' Acrylic policy.</summary>
internal sealed class GlassBackdrop : SystemBackdrop
{
    private readonly float _blurAmount;
    internal GlassBackdrop(float blurAmount=24) => _blurAmount=blurAmount;
    private global::Windows.UI.Composition.Compositor? _compositor;
    private global::Windows.UI.Composition.CompositionBrush? _brush;
    private AlphaWindow? _alpha;
    private IntPtr _hwnd;
    private ICompositionSupportsSystemBackdrop? _target;
    private bool _visible=true;
    private float _opacity=1;
    private float? _appliedOpacity;
    internal static int AppliedTargets { get; private set; }
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target,XamlRoot root)
    {
        base.OnTargetConnected(target,root);
        _hwnd=Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        _alpha=new AlphaWindow(_hwnd);
        bool applied=LocalWindowBlur.Apply(_hwnd,true);
        _compositor=new global::Windows.UI.Composition.Compositor();
        try
        {
            if(!applied)throw new NotSupportedException("Local backdrop unavailable.");
            var blur=new GaussianBlurEffect { BlurAmount=_blurAmount,BorderMode=EffectBorderMode.Hard,Optimization=EffectOptimization.Balanced,Source=new global::Windows.UI.Composition.CompositionEffectSourceParameter("Backdrop") };
            var fade=new OpacityEffect { Name="SurfaceFade",Opacity=1,Source=blur };
            using var factory=_compositor.CreateEffectFactory(fade,new[]{"SurfaceFade.Opacity"});
            var effect=factory.CreateBrush();effect.SetSourceParameter("Backdrop",_compositor.CreateBackdropBrush());_brush=effect;
        }
        catch(Exception ex)
        {
            _brush=_compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(255,20,22,27));
            DockApp.Current.Log.Warn("Glass fallback: "+ex.Message);
        }
        _target=target;_appliedOpacity=null;ResetOpacity(_visible?_opacity:0);target.SystemBackdrop=_visible?_brush:null;
        if(!_visible)LocalWindowBlur.Apply(_hwnd,false);
        if(applied)AppliedTargets++;
        DockApp.Current.Log.Info("Local glass target: "+NativeMethods.GetClass(_hwnd)+", hwnd="+_hwnd+", applied="+applied);
    }
    internal void SetVisible(bool visible)
    {
        if(_visible==visible)return;
        _visible=visible;
        if(_target is null)return;
        if(!visible)
        {
            ResetOpacity(0);
            _target.SystemBackdrop=null;LocalWindowBlur.Apply(_hwnd,false);
        }
        else
        {
            ResetOpacity(_opacity);
            LocalWindowBlur.Apply(_hwnd,true);_target.SystemBackdrop=_brush;
        }
    }
    private void ResetOpacity(float opacity)
    {
        if(_brush is not global::Windows.UI.Composition.CompositionEffectBrush effect)return;
        if(_appliedOpacity==opacity)return;
        effect.StopAnimation("SurfaceFade.Opacity");
        effect.Properties.InsertScalar("SurfaceFade.Opacity",opacity);
        _appliedOpacity=opacity;
    }
    internal void SetOpacity(float opacity){_opacity=Math.Clamp(opacity,0,1);ResetOpacity(_opacity);}
    internal void AnimateOpacity(float opacity,int milliseconds,bool entering=false)
    {
        if(_brush is not global::Windows.UI.Composition.CompositionEffectBrush effect||_compositor is null)return;
        _appliedOpacity=null;
        effect.StopAnimation("SurfaceFade.Opacity");
        if(entering)effect.Properties.InsertScalar("SurfaceFade.Opacity",SurfaceMotion.Enabled?0:1);
        if(!SurfaceMotion.Enabled) { effect.Properties.InsertScalar("SurfaceFade.Opacity",opacity);return; }
        using var ease=_compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(.16f,1),new System.Numerics.Vector2(.3f,1));
        using var fade=_compositor.CreateScalarKeyFrameAnimation();
        fade.Duration=TimeSpan.FromMilliseconds(milliseconds);fade.StopBehavior=global::Windows.UI.Composition.AnimationStopBehavior.LeaveCurrentValue;
        fade.InsertExpressionKeyFrame(0,"this.StartingValue");fade.InsertKeyFrame(1,opacity,ease);
        effect.StartAnimation("SurfaceFade.Opacity",fade);
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        ResetOpacity(0);_target=null;target.SystemBackdrop=null;
        LocalWindowBlur.Apply(_hwnd,false);
        _alpha?.Dispose();_alpha=null;_brush?.Dispose();_brush=null;_compositor?.Dispose();_compositor=null;
        base.OnTargetDisconnected(target);
    }
}



