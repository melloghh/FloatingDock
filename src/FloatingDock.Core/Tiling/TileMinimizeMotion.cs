namespace FloatingDock.Core.Tiling;

/// <summary>Finite dock-directed motion with uniform scaling and a soft curved center path.</summary>
public static class TileMinimizeMotion
{
    public static TileRect FitAtDock(TileRect window,TileRect dock)
    {
        double scale=Math.Min(dock.Width/(double)Math.Max(1,window.Width),dock.Height/(double)Math.Max(1,window.Height));
        int width=Math.Max(1,(int)Math.Round(window.Width*scale)),height=Math.Max(1,(int)Math.Round(window.Height*scale));
        return new((int)Math.Round(dock.CenterX-width/2d),(int)Math.Round(dock.CenterY-height/2d),width,height);
    }
    public static TileRect Sample(TileRect from,TileRect dock,double progress)
    {
        double t=Math.Clamp(progress,0,1);if(t==0)return from;
        var end=FitAtDock(from,dock);if(t==1)return end;
        double ease=t*t*(3-2*t),travel=1-Math.Pow(1-t,3);
        bool horizontal=Math.Abs(dock.CenterX-from.CenterX)>Math.Abs(dock.CenterY-from.CenterY);
        double x=from.CenterX+(dock.CenterX-from.CenterX)*(horizontal?travel:ease);
        double y=from.CenterY+(dock.CenterY-from.CenterY)*(horizontal?ease:travel);
        double finalScale=Math.Min(dock.Width/(double)Math.Max(1,from.Width),dock.Height/(double)Math.Max(1,from.Height));
        double scale=1+(finalScale-1)*ease;
        int width=Math.Max(1,(int)Math.Round(from.Width*scale)),height=Math.Max(1,(int)Math.Round(from.Height*scale));
        return new((int)Math.Round(x-width/2d),(int)Math.Round(y-height/2d),width,height);
    }
}
