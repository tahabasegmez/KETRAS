using Mapsui;
using NetTopologySuite.Geometries;

namespace Ketras.Map.Rendering;

/// <summary>
/// The fixed-wing landing pattern: an orbit beside the approach, left on its tangent, then a
/// straight approach down to touchdown. The approach is drawn as a funnel narrowing onto the
/// touchdown point, because a plain line gives no sense of the approach corridor.
/// </summary>
internal static class LandingPatternGeometry
{
    /// <summary>Half-width of the funnel where it meets the ground, as a fraction of the orbit radius.</summary>
    private const double TouchdownWidthFactor = 0.12;

    internal readonly record struct Pattern(MPoint ApproachStart, MPoint OrbitCenter, Polygon Funnel, LineString Centerline);

    /// <summary>
    /// Where the straight approach begins, <paramref name="approachMapUnits"/> back along the reciprocal of
    /// the approach heading. It is also the pattern's second drag handle.
    /// </summary>
    public static MPoint ApproachStart(MPoint touchdown, double approachHeadingDegrees, double approachMapUnits)
    {
        var (forwardX, forwardY) = MapGeometry.HeadingToUnitVector(approachHeadingDegrees);
        return new MPoint(
            touchdown.X - (forwardX * approachMapUnits),
            touchdown.Y - (forwardY * approachMapUnits));
    }

    /// <summary>Orbit centre one radius beside the approach start: right of the approach when clockwise, left otherwise.</summary>
    public static MPoint OrbitCenter(MPoint approachStart, double approachHeadingDegrees, double radiusMapUnits, bool clockwise)
    {
        var (sideX, sideY) = MapGeometry.HeadingToUnitVector(approachHeadingDegrees + (clockwise ? 90 : 270));
        return new MPoint(
            approachStart.X + (sideX * radiusMapUnits),
            approachStart.Y + (sideY * radiusMapUnits));
    }

    /// <summary>
    /// Builds the pattern for a touchdown point. The orbit is tangent to the approach line at its start, so
    /// the aircraft rolls out of the turn already lined up.
    /// </summary>
    public static Pattern Build(
        MPoint touchdown,
        double approachHeadingDegrees,
        double approachMapUnits,
        double radiusMapUnits,
        bool clockwise)
    {
        var start = ApproachStart(touchdown, approachHeadingDegrees, approachMapUnits);
        var center = OrbitCenter(start, approachHeadingDegrees, radiusMapUnits, clockwise);

        var (sideX, sideY) = MapGeometry.HeadingToUnitVector(approachHeadingDegrees + 90);
        var nearHalfWidth = radiusMapUnits * TouchdownWidthFactor;

        var funnel = new Polygon(new LinearRing(
        [
            new Coordinate(start.X + (sideX * radiusMapUnits), start.Y + (sideY * radiusMapUnits)),
            new Coordinate(touchdown.X + (sideX * nearHalfWidth), touchdown.Y + (sideY * nearHalfWidth)),
            new Coordinate(touchdown.X - (sideX * nearHalfWidth), touchdown.Y - (sideY * nearHalfWidth)),
            new Coordinate(start.X - (sideX * radiusMapUnits), start.Y - (sideY * radiusMapUnits)),
            new Coordinate(start.X + (sideX * radiusMapUnits), start.Y + (sideY * radiusMapUnits))
        ]));

        var centerline = new LineString(
        [
            new Coordinate(start.X, start.Y),
            new Coordinate(touchdown.X, touchdown.Y)
        ]);

        return new Pattern(start, center, funnel, centerline);
    }
}
