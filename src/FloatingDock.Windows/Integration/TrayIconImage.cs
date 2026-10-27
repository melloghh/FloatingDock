using System.Buffers.Binary;

namespace FloatingDock.Windows.Integration;

/// <summary>Explorer snapshots must never be enlarged past their real pixel resolution.</summary>
public static class TrayIconImage
{
    private static ReadOnlySpan<byte> Signature=>[137,80,78,71,13,10,26,10];
    public static int PixelSize(byte[]? png)
    {
        if(png is null||png.Length<24||!png.AsSpan(0,8).SequenceEqual(Signature))return 0;
        int width=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4)),height=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4));
        return width is >0 and <=512&&height is >0 and <=512?Math.Min(width,height):0;
    }
    public static double DisplaySize(byte[]? png,double scale)=>Math.Min(24,PixelSize(png)/Math.Max(1,scale));
}
