using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Controls.Map.Rendering;

using Mapsui;

namespace Ketras.Controls.Map.Interaction;

/// <summary>Which handle of a mission item the pointer grabbed.</summary>
internal enum MissionHandleKind
{
    /// <summary>The item's own position.</summary>
    Item,

    /// <summary>The start of a landing pattern's approach, which swings and stretches it.</summary>
    LandingApproachStart,

    /// <summary>The point due east on a selected loiter's orbit, which changes its radius.</summary>
    OrbitRadius
}

/// <summary>Which handle of a fence the pointer grabbed.</summary>
internal enum FenceHandleKind
{
    PolygonVertex,
    CircleCenter,
    CircleRadius
}

internal sealed record MissionDragTarget(MissionItem Item, MissionHandleKind Kind);

internal sealed record FenceDragTarget(Fence Fence, FenceHandleKind Kind, int VertexIndex = -1);

/// <summary>
/// Picks map features under a pointer. Thresholds arrive in map units: only the caller knows
/// the viewport resolution needed to convert a screen-space grab radius.
/// </summary>
internal static class MapHitTester
{
    /// <summary>
    /// Nearest grabbable mission handle within the threshold, or null. A landing pattern
    /// exposes two handles - touchdown and orbit centre - and both compete on distance, so
    /// the operator gets whichever they aimed at. The selected loiter also exposes its radius handle.
    /// </summary>
    public static MissionDragTarget? FindMissionHandle(
        IReadOnlyList<MissionItem> items,
        MPoint world,
        double thresholdMapUnits,
        Guid? selectedId = null)
    {
        var nearestDistance = double.MaxValue;
        MissionDragTarget? nearest = null;

        void Test(MissionItem item, MPoint candidate, MissionHandleKind kind)
        {
            var distance = MapGeometry.DistanceBetween(candidate, world);
            if (distance > thresholdMapUnits || distance >= nearestDistance)
            {
                return;
            }

            nearestDistance = distance;
            nearest = new MissionDragTarget(item, kind);
        }

        foreach (var item in items)
        {
            if (item.Location is not { } location)
            {
                continue;
            }

            var anchor = MapGeometry.ToMercator(location);
            Test(item, anchor, MissionHandleKind.Item);

            if (item.Type == MissionItemType.LandingPattern)
            {
                var approach = MapGeometry.MetersToMapUnits(Math.Max(item.Param3, 1), location.Latitude);
                Test(item, LandingPatternGeometry.ApproachStart(anchor, item.Param4, approach), MissionHandleKind.LandingApproachStart);
            }

            if (item.Id == selectedId && item.Type != MissionItemType.LandingPattern && MissionItemValues.LoiterRadiusMeters(item) is { } radius)
            {
                Test(item, OrbitRadiusHandle(anchor, radius, location.Latitude), MissionHandleKind.OrbitRadius);
            }
        }

        return nearest;
    }

    /// <summary>Where a loiter's radius handle sits: due east of the centre, on the orbit.</summary>
    public static MPoint OrbitRadiusHandle(MPoint center, double radiusMeters, double latitude) =>
        new(center.X + MapGeometry.MetersToMapUnits(radiusMeters, latitude), center.Y);

    /// <summary>
    /// Nearest grabbable fence handle. A circle's centre wins ties against its radius handle,
    /// so a small circle stays movable rather than only resizable.
    /// </summary>
    public static FenceDragTarget? FindFenceHandle(
        IReadOnlyList<Fence> fences,
        MPoint world,
        double thresholdMapUnits)
    {
        var nearestDistance = double.MaxValue;
        FenceDragTarget? nearest = null;

        void Test(MPoint candidate, FenceDragTarget target)
        {
            var distance = MapGeometry.DistanceBetween(candidate, world);
            if (distance > thresholdMapUnits || distance >= nearestDistance)
            {
                return;
            }

            nearestDistance = distance;
            nearest = target;
        }

        foreach (var fence in fences)
        {
            switch (fence.Area)
            {
                case CircleArea circle:
                {
                    var center = MapGeometry.ToMercator(circle.Center);
                    Test(center, new FenceDragTarget(fence, FenceHandleKind.CircleCenter));

                    var radius = MapGeometry.MetersToMapUnits(Math.Max(circle.RadiusMeters, 1), circle.Center.Latitude);
                    Test(new MPoint(center.X + radius, center.Y), new FenceDragTarget(fence, FenceHandleKind.CircleRadius));
                    break;
                }

                case PolygonArea polygon:
                {
                    for (var i = 0; i < polygon.Vertices.Length; i++)
                    {
                        Test(MapGeometry.ToMercator(polygon.Vertices[i]), new FenceDragTarget(fence, FenceHandleKind.PolygonVertex, i));
                    }

                    break;
                }
            }
        }

        return nearest;
    }
}
