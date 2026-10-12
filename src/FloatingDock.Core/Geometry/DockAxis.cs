using FloatingDock.Core.Configuration;
using System.Numerics;

namespace FloatingDock.Core.Geometry;

/// <summary>Maps the unchanged horizontal layout into an edge-local frame. Icons are positioned, never rotated.</summary>
public readonly record struct DockAxis(DockEdge Edge, float CrossExtent)
{
    public bool Vertical => Edge != DockEdge.Bottom;
    public Vector2 ToPhysical(float along, float inward) => Edge switch
    {
        DockEdge.Left => new(CrossExtent - inward, along),
        DockEdge.Right => new(inward, along),
        _ => new(along, inward),
    };
    public Vector2 ToLogical(float x, float y) => Edge switch
    {
        DockEdge.Left => new(y, CrossExtent - x),
        DockEdge.Right => new(y, x),
        _ => new(x, y),
    };
    public Rect ToPhysical(Rect rect) => Edge switch
    {
        DockEdge.Left => new(CrossExtent - rect.Bottom, rect.X, rect.Height, rect.Width),
        DockEdge.Right => new(rect.Y, rect.X, rect.Height, rect.Width),
        _ => rect,
    };
    public Matrix4x4 PlateTransform => Edge switch
    {
        DockEdge.Left => new(0,1,0,0, -1,0,0,0, 0,0,1,0, CrossExtent,0,0,1),
        DockEdge.Right => new(0,1,0,0, 1,0,0,0, 0,0,1,0, 0,0,0,1),
        _ => Matrix4x4.Identity,
    };
    public Vector3 HideOffset(float distance) => Edge switch
    {
        DockEdge.Left => new(-distance,0,0),
        DockEdge.Right => new(distance,0,0),
        _ => new(0,distance,0),
    };
}
