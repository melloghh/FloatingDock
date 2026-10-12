using System.Numerics;
using System.Text.Json;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Geometry;
using Xunit;

namespace FloatingDock.Core.Tests;
public class Dock113Tests
{
    [Theory]
    [InlineData(DockEdge.Bottom, 70f, 140f, 70f, 140f, 42f, 18f, 0f, 50f)]
    [InlineData(DockEdge.Left, 70f, 140f, 142f, 70f, 18f, 42f, -50f, 0f)]
    [InlineData(DockEdge.Right, 70f, 140f, 140f, 70f, 18f, 42f, 50f, 0f)]
    public void RenderingPointerAndHideUseTheSamePhysicalFrame(DockEdge edge,float u,float v,float x,float y,float w,float h,float hideX,float hideY)
    {
        var axis=new DockAxis(edge,300);
        var logical=new Rect(u,v,42,18);
        var rect=axis.ToPhysical(logical);
        Assert.Equal(new Vector2(x,y),new Vector2(rect.X,rect.Y));
        Assert.Equal(w,rect.Width);Assert.Equal(h,rect.Height);
        foreach(var point in new[]{new Vector2(u,v),new Vector2(u+42,v+18),new Vector2(u+21,v+9)})
        {
            var screen=axis.ToPhysical(point.X,point.Y);
            Assert.Equal(point,axis.ToLogical(screen.X,screen.Y));
            Assert.Equal(screen,new Vector2(Vector3.Transform(new Vector3(point,0),axis.PlateTransform).X,Vector3.Transform(new Vector3(point,0),axis.PlateTransform).Y));
            Assert.True(screen.X>=rect.X&&screen.X<=rect.Right&&screen.Y>=rect.Y&&screen.Y<=rect.Bottom);
        }
        Assert.Equal(new Vector3(hideX,hideY,0),axis.HideOffset(50));
    }
    [Theory]
    [InlineData(DockEdge.Bottom)]
    [InlineData(DockEdge.Left)]
    [InlineData(DockEdge.Right)]
    public void SettingsRoundTripPreservesDockEdge(DockEdge edge)
    {
        var input=new DockSettings { Edge=edge,IconSize=46,BottomOffset=19 };
        var restored=JsonSerializer.Deserialize<DockSettings>(JsonSerializer.Serialize(input))!.Normalize();
        Assert.Equal(edge,restored.Edge);Assert.Equal(46,restored.IconSize);Assert.Equal(19,restored.BottomOffset);
    }
    [Fact] public void OldAndInvalidSettingsKeepBottomAsDefault()
    {
        Assert.Equal(DockEdge.Bottom,JsonSerializer.Deserialize<DockSettings>("{\"IconSize\":46}")!.Normalize().Edge);
        Assert.Equal(DockEdge.Bottom,new DockSettings { Edge=(DockEdge)99 }.Normalize().Edge);
    }
}
