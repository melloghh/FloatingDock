using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FloatingDock.UI.Views;

/// <summary>Finite compositor transitions: no dispatcher frame loop or bitmap capture.</summary>
internal static class SurfaceMotion
{
    private static readonly global::Windows.UI.ViewManagement.UISettings Settings = new();
    internal static bool Enabled => Settings.AnimationsEnabled && DockApp.Current.Settings.Current.MenuAnimations;
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
    internal static void SlideContent(FrameworkElement element,int milliseconds)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element,true);
        var visual=ElementCompositionPreview.GetElementVisual(element);visual.StopAnimation("Translation");
        if(!Enabled){visual.Properties.InsertVector3("Translation",Vector3.Zero);return;}
        var compositor=visual.Compositor;
        using var ease=compositor.CreateCubicBezierEasingFunction(new Vector2(.16f,1),new Vector2(.3f,1));
        using var animation=compositor.CreateVector3KeyFrameAnimation();animation.Duration=TimeSpan.FromMilliseconds(milliseconds);
        animation.InsertKeyFrame(0,new Vector3(0,10,0));animation.InsertKeyFrame(1,Vector3.Zero,ease);
        visual.StartAnimation("Translation",animation);
    }
    internal static void MenuContent(FrameworkElement element,int milliseconds,bool entering)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element,true);
        var visual=ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint=new Vector3((float)element.ActualWidth*.85f,0,0);
        visual.StopAnimation("Translation");
        if(entering)Reset(visual,Enabled?0:1,Enabled?.96f:1);
        Animate(visual,entering?1:0,entering?1:.98f,milliseconds);
        if(!Enabled){visual.Properties.InsertVector3("Translation",Vector3.Zero);return;}
        using var ease=visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f,.8f),new Vector2(.2f,1));
        using var slide=visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.Duration=TimeSpan.FromMilliseconds(milliseconds);
        if(entering)slide.InsertKeyFrame(0,new Vector3(0,-10,0));
        else slide.InsertExpressionKeyFrame(0,"this.StartingValue");
        slide.InsertKeyFrame(1,entering?Vector3.Zero:new Vector3(0,-6,0),ease);
        visual.StartAnimation("Translation",slide);
        if(entering&&element is Microsoft.UI.Xaml.Controls.Panel panel)
        {
            int index=0;
            foreach(var child in panel.Children)
            {
                var row=ElementCompositionPreview.GetElementVisual(child);
                row.StopAnimation("Opacity");row.Opacity=0;
                using var fade=row.Compositor.CreateScalarKeyFrameAnimation();
                fade.DelayTime=TimeSpan.FromMilliseconds(Math.Min(index++*12,60));
                fade.Duration=TimeSpan.FromMilliseconds(Math.Max(60,milliseconds-60));
                fade.InsertKeyFrame(0,0);fade.InsertKeyFrame(1,1,ease);row.StartAnimation("Opacity",fade);
            }
        }
    }
    internal static void Enter(FrameworkElement element, int milliseconds=220,float initialScale=.985f)
    {
        var visual=ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint=new Vector3((float)element.ActualWidth/2,(float)element.ActualHeight/2,0);
        Reset(visual,Enabled?0:1,Enabled?initialScale:1);
        Animate(visual,1,1,milliseconds);
    }
    internal static void Reveal(FrameworkElement element,int index=0)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element,true);
        var visual=ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");visual.StopAnimation("Translation");
        if(!Enabled){visual.Opacity=1;visual.Properties.InsertVector3("Translation",Vector3.Zero);return;}
        using var ease=visual.Compositor.CreateCubicBezierEasingFunction(new(.16f,1),new(.3f,1));
        using var fade=visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.DelayTime=TimeSpan.FromMilliseconds(Math.Min(index*7,84));fade.Duration=TimeSpan.FromMilliseconds(240);
        fade.InsertKeyFrame(0,0);fade.InsertKeyFrame(1,1,ease);visual.Opacity=0;
        using var slide=visual.Compositor.CreateVector3KeyFrameAnimation();slide.DelayTime=fade.DelayTime;slide.Duration=TimeSpan.FromMilliseconds(320);
        slide.InsertKeyFrame(0,new Vector3(0,14,0));slide.InsertKeyFrame(1,Vector3.Zero,ease);
        visual.StartAnimation("Opacity",fade);visual.StartAnimation("Translation",slide);
    }
}
