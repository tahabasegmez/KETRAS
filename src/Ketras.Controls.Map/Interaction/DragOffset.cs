using Mapsui;

namespace Ketras.Controls.Map.Interaction;

/// <summary>
/// Gap between where the pointer grabbed and the thing's own anchor. Without it, picking up a
/// waypoint by its edge would snap its centre onto the cursor.
/// </summary>
internal readonly record struct DragOffset(double X, double Y)
{
    public static DragOffset Between(MPoint anchor, MPoint pointer) =>
        new(anchor.X - pointer.X, anchor.Y - pointer.Y);

    public MPoint Apply(MPoint pointer) => new(pointer.X + X, pointer.Y + Y);
}
