using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FloatingDock.UI.Views;

/// <summary>Finite compositor transitions: no dispatcher frame loop or bitmap capture.</summary>
internal static class SurfaceMotion
{
    private static readonly global::Windows.UI.ViewManagement.UISettings Settings = new();
    internal static bool Enabled => Settings.AnimationsEnabled;
    internal static int Duration(int milliseconds) => Enabled ? milliseconds : 0;
    internal static void Reset(Visual visual,float opacity=1,float scale=1)
    {
        visual.StopAnimation("Opacity");visual.StopAnimation("Scale");
        visual.Opacity=opacity;visual.Scale=new Vector3(scale,scale,1);
    }

    internal static void Animate(Visual visual, float opacity, float scale, int milliseconds)
    {
        visual.StopAnimation("Opacity"); visual.StopAnimation("Scale");
        if (!Enabled) { visual.Opacity=opacity;visual.Scale=new Vector3(scale,scale,1);return; }
        var compositor=visual.Compositor;
        using var ease=compositor.CreateCubicBezierEasingFunction(new Vector2(.16f,1),new Vector2(.3f,1));
        using var fade=compositor.CreateScalarKeyFrameAnimation();
        fade.Duration=TimeSpan.FromMilliseconds(milliseconds);fade.StopBehavior=AnimationStopBehavior.LeaveCurrentValue;
        fade.InsertExpressionKeyFrame(0,"this.StartingValue");fade.InsertKeyFrame(1,opacity,ease);
        using var zoom=compositor.CreateVector3KeyFrameAnimation();
        zoom.Duration=fade.Duration;zoom.StopBehavior=AnimationStopBehavior.LeaveCurrentValue;
        zoom.InsertExpressionKeyFrame(0,"this.StartingValue");zoom.InsertKeyFrame(1,new Vector3(scale,scale,1),ease);
        visual.StartAnimation("Opacity",fade);visual.StartAnimation("Scale",zoom);
    }
    internal static void Enter(FrameworkElement element, int milliseconds=220,float initialScale=.985f)
    {
        var visual=ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint=new Vector3((float)element.ActualWidth/2,(float)element.ActualHeight/2,0);
        Reset(visual,Enabled?0:1,Enabled?initialScale:1);
        Animate(visual,1,1,milliseconds);
    }
}
