namespace FloatingDock.Core.Tiling;

/// <summary>A finite, dock-directed unfolding path. Size and travel share one phase, with zero endpoint velocity.</summary>
public static class TileDockMotion
{
    public static TileRect Sample(TileRect from,TileRect to,double progress,bool restoring)
    {
        double t=Math.Clamp(progress,0,1);if(t==0)return from;if(t==1)return to;
        double p=restoring?(1-(1+9*t)*Math.Exp(-9*t))/(1-10*Math.Exp(-9)):
            t*t*t*(10+t*(-15+6*t));
        bool vertical=Math.Abs(to.CenterY-from.CenterY)>=Math.Abs(to.CenterX-from.CenterX);
        // The same corner is used in both directions: move away from the dock edge before growing into a tile.
        double cx=vertical?(restoring?from.CenterX:to.CenterX):(restoring?to.CenterX:from.CenterX);
        double cy=vertical?(restoring?to.CenterY:from.CenterY):(restoring?from.CenterY:to.CenterY);
        double q=1-p;
        double x=q*q*from.CenterX+2*q*p*cx+p*p*to.CenterX;
        double y=q*q*from.CenterY+2*q*p*cy+p*p*to.CenterY;
        int width=Math.Max(1,(int)Math.Round(from.Width+(to.Width-from.Width)*p));
        int height=Math.Max(1,(int)Math.Round(from.Height+(to.Height-from.Height)*p));
        return new((int)Math.Round(x-width/2d),(int)Math.Round(y-height/2d),width,height);
    }
}
