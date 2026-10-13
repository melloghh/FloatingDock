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
    private readonly GlassBackdrop _glass;
    private readonly FrameScheduler _clock;
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

    internal MenuSurfaceMotion(IntPtr hwnd, FrameworkElement root, GlassBackdrop glass)
    {
        _hwnd=hwnd;_root=root;_glass=glass;
        ElementCompositionPreview.SetIsTranslationEnabled(root,true);
        _clock=new(action=>DockApp.Current.UiQueue.TryEnqueue(()=>action()),Tick);
    }

    internal void SetGeometry(int width,int height,double dpi,double pivot)
    {
        _width=width;_height=height;_dpi=dpi;_pivot=Math.Clamp(pivot,0,width/dpi);
        Apply();
    }

    internal void Prepare(float progress)
    {
        Stop();Progress=Math.Clamp(progress,0,1);
        var visual=ElementCompositionPreview.GetElementVisual(_root);
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
        _clock.SetFrameRate(DockApp.Current.Settings.Current.TopBarAnimationFps);_clock.Start();
    }

    private void Tick()
    {
        double t=Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds/_duration,0,1);
        // A fast release with a damped settling tail; the exit accelerates towards its button.
        double eased=_to>_from?(1-(1+9*t)*Math.Exp(-9*t))/(1-10*Math.Exp(-9)):t*t;
        Progress=(float)(_from+(_to-_from)*eased);Apply();
        if(t>=1)Finish();
    }

    private void Apply()
    {
        if(_width<=0||_height<=0)return;
        float p=Progress,sx=.78f+.22f*p,sy=.55f+.45f*p,y=-12*(1-p);
        var visual=ElementCompositionPreview.GetElementVisual(_root);
        visual.CenterPoint=new Vector3((float)_pivot,0,0);
        visual.Scale=new Vector3(sx,sy,1);
        visual.Properties.InsertVector3("Translation",new Vector3(0,y,0));
        visual.Opacity=Math.Clamp(p*3,0,1);_glass.SetOpacity(visual.Opacity);
        int left=(int)Math.Round(_pivot*_dpi*(1-sx));
        int top=Math.Max(0,(int)Math.Round(y*_dpi));
        int right=Math.Min(_width,(int)Math.Round(left+_width*sx));
        int bottom=Math.Clamp((int)Math.Round(y*_dpi+_height*sy),top+1,_height);
        int radius=(int)Math.Round(16*_dpi*Math.Min(sx,sy));
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
        _clock.Stop();var completed=_completed;_completed=null;completed?.Invoke();
    }
    internal void Stop(){_clock.Stop();_completed=null;}
    public void Dispose(){Stop();_clock.Dispose();}
}
