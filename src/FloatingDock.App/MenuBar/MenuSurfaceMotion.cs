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
    private readonly Microsoft.UI.Composition.Visual _visual;
    private readonly Microsoft.UI.Composition.CompositionPropertySet _properties;
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
    private Vector3 _scale = new(float.NaN), _translation = new(float.NaN);
    private float _opacity = float.NaN;
    internal float Progress { get; private set; }
    internal bool IsRunning => _clock.IsRunning;
    internal (int L, int T, int R, int B, int Radius)? Clip => _clip;
    internal bool VisualMatchesProgressForDiagnostics =>
        Vector3.Distance(_visual.Scale,new Vector3(_minX+(1-_minX)*Progress,_minY+(1-_minY)*Progress,1))<.0001f &&
        Math.Abs(_visual.Opacity-Math.Clamp(Progress*3,0,1))<.0001f;

    internal MenuSurfaceMotion(IntPtr hwnd, FrameworkElement root, GlassBackdrop glass,float minX=.78f,float minY=.55f,float lift=-12,int radius=16)
    {
        _hwnd=hwnd;_glass=glass;
        _minX=minX;_minY=minY;_lift=lift;_radius=radius;
        ElementCompositionPreview.SetIsTranslationEnabled(root,true);
        _visual=ElementCompositionPreview.GetElementVisual(root);
        _properties=_visual.Properties;
        _clock=FloatingDock.UI.Services.AnimationClock.Create(DockApp.Current.UiQueue,Tick);
    }

    internal void SetGeometry(int width,int height,double dpi,double pivot)
    {
        _width=width;_height=height;_dpi=dpi;_pivot=Math.Clamp(pivot,0,width/dpi);
        _visual.CenterPoint=new Vector3((float)_pivot,0,0);
        Apply();
    }

    internal void Prepare(float progress)
    {
        // HidePopup may reset this visual outside the motion controller.
        _scale=new(float.NaN);_translation=new(float.NaN);_opacity=float.NaN;
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
        // XAML and the HWND glass region must use the same presentation sample.
        // Independent compositor animations can run ahead of the native clip.
        _clock.SetFrameRate(DockApp.Current.Settings.Current.TopBarAnimationFps);_clock.Start();
    }

    private double Ease(double t)=>_to>_from?(1-(1+9*t)*Math.Exp(-9*t))/(1-10*Math.Exp(-9)):t*t;
    private void Tick()
    {
        double t=Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds/_duration,0,1);
        // A fast release with a damped settling tail; the exit accelerates towards its button.
        Progress=(float)(_from+(_to-_from)*Ease(t));Apply();
        if(t>=1)Finish();
    }

    private void Apply()
    {
        if(_width<=0||_height<=0)return;
        float p=Progress,sx=_minX+(1-_minX)*p,sy=_minY+(1-_minY)*p,y=_lift*(1-p);
        var visual=_visual;
        var scale=new Vector3(sx,sy,1);var translation=new Vector3(0,y,0);float opacity=Math.Clamp(p*3,0,1);
        if(_scale!=scale){visual.Scale=scale;_scale=scale;}
        if(_translation!=translation){_properties.InsertVector3("Translation",translation);_translation=translation;}
        if(_opacity!=opacity){visual.Opacity=opacity;_opacity=opacity;}
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
        if(NativeMethods.SetWindowRgn(_hwnd,region,true)==0)
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
        // Keep the last presented pose when reversing, even if the UI was busy.
        _clock.Stop();StopVisual();Apply();_completed=null;
    }
    public void Dispose(){Stop();_clock.Dispose();}
}
