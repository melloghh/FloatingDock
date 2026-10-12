namespace FloatingDock.Core.Tiling;

/// <summary>Unknown native constraints must not turn a full application into a sliver.</summary>
public static class TileMinimum
{
    public static (int Width,int Height) Resolve(int nativeWidth,int nativeHeight,double scale=1)
    {
        scale=double.IsFinite(scale)?Math.Clamp(scale,.5,4):1;
        return (nativeWidth>0?Math.Max(120,nativeWidth):(int)Math.Ceiling(480*scale),
            nativeHeight>0?Math.Max(80,nativeHeight):(int)Math.Ceiling(320*scale));
    }
}
