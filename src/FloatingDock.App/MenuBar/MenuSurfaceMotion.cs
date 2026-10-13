using System.Diagnostics;
using System.Numerics;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FloatingDock.UI.MenuBar;

/// <summary>Moves the XAML plate and its native glass clip together, without resizing or relaying out the HWND.</summary>
internal sealed class MenuSurfaceMotion : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly FrameworkElement _root;
    private readonly Microsoft.UI.Composition.Visual _visual;
    private readonly GlassBackdrop _glass;
    private readonly FrameScheduler _clock;
    private readonly float _minX,_minY,_lift;
    private readonly int _radius;
    private int _width, _height;
    private double _dpi, _pivot;
    private long _started;
    private int _duration;
    private float _from, _to;
    private Action? _completed;
    private (int L, int T, int R, int B, int Radius)? _clip;
    internal float Progress { get; private set; }
    internal bool IsRunning => _clock.IsRunning;
    internal (int L, int T, int R, int B, int Radius)? Clip => _clip;

    internal MenuSurfaceMotion(IntPtr hwnd, FrameworkElement root, GlassBackdrop glass,float minX=.78f,float minY=.55f,float lift=-12,int radius=16)
    {
        _hwnd=hwnd;_root=root;_glass=glass;
        _minX=minX;_minY=minY;_lift=lift;_radius=radius;
        ElementCompositionPreview.SetIsTranslationEnabled(root,true);
        _visual=ElementCompositionPreview.GetElementVisual(root);
        _clock=new(action=>DockApp.Current.UiQueue.TryEnqueue(()=>action()),Tick);
    }

    internal void SetGeometry(int width,int height,double dpi,double pivot)
    {
        _width=width;_height=height;_dpi=dpi;_pivot=Math.Clamp(pivot,0,width/dpi);
        _visual.CenterPoint=new Vector3((float)_pivot,0,0);
        Apply();
    }

    internal void Prepare(float progress)
    {
        Stop();Progress=Math.Clamp(progress,0,1);
        var visual=_visual;
        visual.StopAnimation("Scale");visual.StopAnimation("Opacity");visual.StopAnimation("Translation");
        Apply();
    }

    internal void Play(bool entering,int milliseconds,Action? completed=null)
    {
        Stop();_completed=completed;_from=Progress;_to=entering?1:0;
        if(!SurfaceMotion.Enabled)
        {
            Progress=_to;Apply();Finish();return;
        }
        // Reversing an interrupted transition continues from the current surface, not from a preset pose.
        _duration=Math.Max(50,(int)(milliseconds*Math.Max(.25f,Math.Abs(_to-_from))));
        _started=Stopwatch.GetTimestamp();
        AnimateVisual();
        // Visual motion runs on the compositor at display refresh rate. The
        // native glass boundary needs only a bounded clip synchronization clock.
        _clock.SetFrameRate(Math.Min(60,DockApp.Current.Settings.Current.TopBarAnimationFps));_clock.Start();
    }

    private double Ease(double t)=>_to>_from?(1-(1+9*t)*Math.Exp(-9*t))/(1-10*Math.Exp(-9)):t*t;
    private void AnimateVisual()
    {
        var compositor=_visual.Compositor;
        using var scale=compositor.CreateVector3KeyFrameAnimation();
        using var translation=compositor.CreateVector3KeyFrameAnimation();
        using var opacity=compositor.CreateScalarKeyFrameAnimation();
        scale.Duration=translation.Duration=opacity.Duration=TimeSpan.FromMilliseconds(_duration);
        scale.StopBehavior=translation.StopBehavior=opacity.StopBehavior=Microsoft.UI.Composition.AnimationStopBehavior.LeaveCurrentValue;
        using var linear=compositor.CreateLinearEasingFunction();
        for(int i=0;i<=24;i++)
        {
            float t=i/24f,p=(float)(_from+(_to-_from)*Ease(t));
            scale.InsertKeyFrame(t,new Vector3(_minX+(1-_minX)*p,_minY+(1-_minY)*p,1),linear);
            translation.InsertKeyFrame(t,new Vector3(0,_lift*(1-p),0),linear);
            opacity.InsertKeyFrame(t,Math.Clamp(p*3,0,1),linear);
        }
        _visual.StartAnimation("Scale",scale);_visual.StartAnimation("Translation",translation);_visual.StartAnimation("Opacity",opacity);
    }

    private void Tick()
    {
        double t=Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds/_duration,0,1);
        // A fast release with a damped settling tail; the exit accelerates towards its button.
        Progress=(float)(_from+(_to-_from)*Ease(t));Apply(updateVisual:false);
        if(t>=1)Finish();
    }

    private void Apply(bool updateVisual=true)
    {
        if(_width<=0||_height<=0)return;
        float p=Progress,sx=_minX+(1-_minX)*p,sy=_minY+(1-_minY)*p,y=_lift*(1-p);
        var visual=_visual;
        if(updateVisual)
        {
            visual.Scale=new Vector3(sx,sy,1);
            visual.Properties.InsertVector3("Translation",new Vector3(0,y,0));
            visual.Opacity=Math.Clamp(p*3,0,1);
        }
        _glass.SetOpacity(Math.Clamp(p*3,0,1));
        int left=(int)Math.Round(_pivot*_dpi*(1-sx));
        int top=Math.Max(0,(int)Math.Round(y*_dpi));
        int right=Math.Min(_width,(int)Math.Round(left+_width*sx));
        int bottom=Math.Clamp((int)Math.Round(y*_dpi+_height*sy),top+1,_height);
        int radius=(int)Math.Round(_radius*_dpi*Math.Min(sx,sy));
        var clip=(left,top,right,bottom,radius);
        if(_clip==clip)return;
        var region=NativeMethods.CreateRoundRectRgn(left,top,right+1,bottom+1,radius*2,radius*2);
        if(region==IntPtr.Zero)throw new InvalidOperationException("Could not create animated menu region.");
        // The compositor already redraws the transformed plate. Invalidating
        // the whole XAML window here adds a second repaint on every frame.
        if(NativeMethods.SetWindowRgn(_hwnd,region,false)==0)
        {NativeMethods.DeleteObject(region);throw new InvalidOperationException("Could not apply animated menu region.");}
        _clip=clip;
    }

    private void Finish()
    {
        _clock.Stop();StopVisual();Apply();var completed=_completed;_completed=null;completed?.Invoke();
    }
    private void StopVisual(){_visual.StopAnimation("Scale");_visual.StopAnimation("Opacity");_visual.StopAnimation("Translation");}
    internal void Stop()
    {
        if(_clock.IsRunning)
        {
            double t=Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds/_duration,0,1);
            Progress=(float)(_from+(_to-_from)*Ease(t));
        }
        _clock.Stop();StopVisual();Apply();_completed=null;
    }
    public void Dispose(){Stop();_clock.Dispose();}
}
