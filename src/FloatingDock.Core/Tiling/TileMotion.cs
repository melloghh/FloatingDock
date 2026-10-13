namespace FloatingDock.Core.Tiling;

public readonly record struct TileMotionSample(TileRect Bounds,double VelocityX,double VelocityY,double VelocityWidth=0,double VelocityHeight=0);

/// <summary>Finite placement path with a gentle start and velocity continuity on retarget.</summary>
public sealed class TileMotion
{
    public TileRect From { get; }
    public TileRect Target { get; }
    public double StartedAt { get; }
    public int Duration { get; }
    private readonly double _vx,_vy;
    private readonly double _vw,_vh;
    private readonly bool _interpolateSize;
    public TileMotion(TileRect from,TileRect target,double startedAt,int duration,double velocityX=0,double velocityY=0,bool interpolateSize=false,double velocityWidth=0,double velocityHeight=0)
    {
        From=from;Target=target;StartedAt=startedAt;Duration=Math.Max(1,duration);
        double Clamp(double velocity,int delta)=>Math.Sign(velocity)*Math.Min(Math.Abs(velocity),3d*Math.Max(8,Math.Abs(delta))/Duration);
        _vx=Clamp(velocityX,target.X-from.X);_vy=Clamp(velocityY,target.Y-from.Y);
        _interpolateSize=interpolateSize;_vw=Clamp(velocityWidth,target.Width-from.Width);_vh=Clamp(velocityHeight,target.Height-from.Height);
    }
    public bool Complete(double now)=>now-StartedAt>=Duration;
    public TileMotionSample Sample(double now)
    {
        double p=Math.Clamp((now-StartedAt)/Duration,0,1);
        (int Position,double Velocity) Axis(int start,int end,double velocity)
        {
            double delta=end-start,position,speed;
            if(velocity==0)
            {
                double ease=p*p*p*(10+p*(-15+6*p));
                position=start+delta*ease;speed=delta*30*p*p*(1-p)*(1-p)/Duration;
            }
            else
            {
                double tangent=velocity*Duration;
                position=start+delta*(3*p*p-2*p*p*p)+tangent*(p*p*p-2*p*p+p);
                speed=(delta*(6*p-6*p*p)+tangent*(3*p*p-4*p+1))/Duration;
            }
            return ((int)Math.Round(position),speed);
        }
        var x=Axis(From.X,Target.X,_vx);var y=Axis(From.Y,Target.Y,_vy);
        var width=_interpolateSize?Axis(From.Width,Target.Width,_vw):(Target.Width,0d);
        var height=_interpolateSize?Axis(From.Height,Target.Height,_vh):(Target.Height,0d);
        return new(new(x.Position,y.Position,Math.Max(1,width.Item1),Math.Max(1,height.Item1)),x.Velocity,y.Velocity,width.Item2,height.Item2);
    }
}
