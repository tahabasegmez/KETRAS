using Mapsui;
using NetTopologySuite.Geometries;

namespace Ketras.Controls.Map.Rendering;

/// <summary>
/// Symbol outlines drawn as real map geometry rather than Mapsui's built-in glyphs.
///
/// A triangle marker tells you where a vehicle is but not much else. A planform silhouette
/// reads as an aircraft, and because it is real geometry it rotates with heading and keeps a
/// constant on-screen size at any zoom.
/// </summary>
internal static class MapSymbols
{
    /// <summary>
    /// Aircraft planform, nose along +y, normalised to a wingspan of 1. Wound clockwise from
    /// the nose down the right side. The fuselage stays visibly narrower than the wings - a
    /// deeper root just reads as a triangle.
    /// </summary>
    private static readonly (double X, double Y)[] AircraftPlanform =
    [
        (0.000, 0.560),   // nose
        (0.028, 0.430),
        (0.038, 0.170),   // wing root, leading edge
        (0.500, 0.055),   // right wingtip, leading edge
        (0.500, -0.020),  // right wingtip, trailing edge
        (0.060, -0.075),  // wing root, trailing edge
        (0.048, -0.330),
        (0.230, -0.420),  // right tailplane
        (0.230, -0.480),
        (0.030, -0.520),
        (0.000, -0.540),  // tail
        (-0.030, -0.520),
        (-0.230, -0.480),
        (-0.230, -0.420),
        (-0.048, -0.330),
        (-0.060, -0.075),
        (-0.500, -0.020),
        (-0.500, 0.055),
        (-0.038, 0.170),
        (-0.028, 0.430)
    ];

    /// <summary>Notched arrow, nose along +y, normalised to a length of 1 (the design catalog's ownship).</summary>
    private static readonly (double X, double Y)[] ArrowOutline =
    [
        (0.000, 0.500),   // nose
        (0.364, -0.409),  // right wing
        (0.000, -0.182),  // notch
        (-0.364, -0.409)  // left wing
    ];

    /// <summary>Arrow centred on a point, rotated to a heading.</summary>
    public static Polygon Arrow(MPoint center, double headingDegrees, double lengthMapUnits) =>
        Outline(ArrowOutline, center, headingDegrees, lengthMapUnits);

    /// <summary>Aircraft silhouette centred on a point, rotated to a heading.</summary>
    public static Polygon Aircraft(MPoint center, double headingDegrees, double spanMapUnits) =>
        Outline(AircraftPlanform, center, headingDegrees, spanMapUnits);

    private static Polygon Outline((double X, double Y)[] shape, MPoint center, double headingDegrees, double scale)
    {
        var ring = new Coordinate[shape.Length + 1];

        for (var i = 0; i < shape.Length; i++)
        {
            var (localX, localY) = shape[i];
            var (x, y) = MapGeometry.RotateByHeading(localX * scale, localY * scale, headingDegrees);
            ring[i] = new Coordinate(center.X + x, center.Y + y);
        }

        ring[^1] = ring[0];
        return new Polygon(new LinearRing(ring));
    }

    /// <summary>Chevron pointing along a heading; shows which way a mission leg or trail runs.</summary>
    public static LineString DirectionChevron(MPoint tip, double headingDegrees, double sizeMapUnits)
    {
        const double wingAngle = 145d;

        var (leftX, leftY) = MapGeometry.RotateByHeading(0, sizeMapUnits, headingDegrees + wingAngle);
        var (rightX, rightY) = MapGeometry.RotateByHeading(0, sizeMapUnits, headingDegrees - wingAngle);

        return new LineString(
        [
            new Coordinate(tip.X + leftX, tip.Y + leftY),
            new Coordinate(tip.X, tip.Y),
            new Coordinate(tip.X + rightX, tip.Y + rightY)
        ]);
    }

    /// <summary>
    /// Square handle used for draggable fence vertices, drawn as geometry so it keeps a
    /// constant on-screen size.
    /// </summary>
    public static Polygon Handle(MPoint center, double sizeMapUnits)
    {
        var half = sizeMapUnits / 2d;

        return new Polygon(new LinearRing(
        [
            new Coordinate(center.X - half, center.Y - half),
            new Coordinate(center.X + half, center.Y - half),
            new Coordinate(center.X + half, center.Y + half),
            new Coordinate(center.X - half, center.Y + half),
            new Coordinate(center.X - half, center.Y - half)
        ]));
    }
}
