using Mapsui;
using NetTopologySuite.Geometries;

namespace Ketras.Map.Rendering;

/// <summary>Orbit rings drawn under loiter items, plus the arrow showing which way they turn.</summary>
internal static class LoiterGeometry
{
    /// <summary>The orbit itself, as a closed ring in map space.</summary>
    public static Polygon Ring(MPoint center, double radiusMapUnits) =>
        MapGeometry.CreateCirclePolygon(center.X, center.Y, radiusMapUnits);

    /// <summary>
    /// Chevron on the ring's north point, pointing along the direction of travel. Without it
    /// a circle says where the aircraft orbits but not which way round.
    /// </summary>
    public static LineString TurnArrow(MPoint center, double radiusMapUnits, bool clockwise, double sizeMapUnits)
    {
        var tip = new MPoint(center.X, center.Y + radiusMapUnits);
        return MapSymbols.DirectionChevron(tip, clockwise ? 90 : 270, sizeMapUnits);
    }
}
