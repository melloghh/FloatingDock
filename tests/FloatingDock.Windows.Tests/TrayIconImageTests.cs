using System.Buffers.Binary;
using FloatingDock.Windows.Integration;
using Xunit;

namespace FloatingDock.Windows.Tests;

public class TrayIconImageTests
{
    private static byte[] Header(int width,int height)
    {
        var bytes=new byte[24];new byte[]{137,80,78,71,13,10,26,10}.CopyTo(bytes,0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),width);BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20),height);return bytes;
    }
    [Theory]
    [InlineData(16,1,16)][InlineData(16,2,8)][InlineData(32,1,24)][InlineData(32,2,16)][InlineData(128,2,24)]
    public void RespectsRealSourceResolution(int pixels,double scale,double expected)=>Assert.Equal(expected,TrayIconImage.DisplaySize(Header(pixels,pixels),scale));
    [Fact] public void RejectsInvalidAndUnboundedImages()
    {
        Assert.Equal(0,TrayIconImage.PixelSize(null));Assert.Equal(0,TrayIconImage.PixelSize(new byte[24]));
        Assert.Equal(0,TrayIconImage.PixelSize(Header(int.MaxValue,16)));Assert.Equal(0,TrayIconImage.PixelSize(Header(16,0)));
    }
}
