using Ketras.Model.Geo;
using Mapsui;
using Mapsui.Projections;
using NetTopologySuite.Geometries;

namespace Ketras.Map.Rendering;

/// <summary>
/// Projection helpers shared by the layer builders and hit tester. The map works in
/// EPSG:3857, the public API in WGS84; this is the only place they meet.
/// </summary>
internal static class MapGeometry
{
    /// <summary>Segments used when approximating a circle as a polygon.</summary>
    public const int CircleSegments = 72;

    public static MPoint ToMercator(double latitude, double longitude)
    {
        var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
        return new MPoint(x, y);
    }

    public static MPoint ToMercator(GeoPoint point) => ToMercator(point.Latitude, point.Longitude);

    public static GeoPoint ToWgs84(MPoint world)
    {
        var (longitude, latitude) = SphericalMercator.ToLonLat(world.X, world.Y);
        return new GeoPoint(latitude, longitude);
    }

    public static GeoPoint ToWgs84(double x, double y)
    {
        var (longitude, latitude) = SphericalMercator.ToLonLat(x, y);
        return new GeoPoint(latitude, longitude);
    }

    public static double DistanceBetween(MPoint a, MPoint b) => DistanceBetween(a, b.X, b.Y);

    public static double DistanceBetween(MPoint point, double x, double y)
    {
        var dx = point.X - x;
        var dy = point.Y - y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static Polygon CreateCirclePolygon(double centerX, double centerY, double radiusMapUnits, int segments = CircleSegments)
    {
        var coordinates = new Coordinate[segments + 1];

        for (var i = 0; i < segments; i++)
        {
            var angle = 2 * Math.PI * i / segments;
            coordinates[i] = new Coordinate(
                centerX + (radiusMapUnits * Math.Cos(angle)),
                centerY + (radiusMapUnits * Math.Sin(angle)));
        }

        coordinates[^1] = coordinates[0];
        return new Polygon(new LinearRing(coordinates));
    }

    public static LineString CreateArcLine(
        double centerX,
        double centerY,
        double radiusMapUnits,
        double startAngleRadians,
        double sweepRadians,
        int segments = CircleSegments)
    {
        var points = new Coordinate[segments + 1];

        for (var i = 0; i <= segments; i++)
        {
            var angle = startAngleRadians + (sweepRadians * ((double)i / segments));
            points[i] = new Coordinate(
                centerX + (radiusMapUnits * Math.Cos(angle)),
                centerY + (radiusMapUnits * Math.Sin(angle)));
        }

        return new LineString(points);
    }

    /// <summary>Compass heading (0 = north, 90 = east) as a unit vector in map space.</summary>
    public static (double X, double Y) HeadingToUnitVector(double headingDegrees)
    {
        var radians = headingDegrees * Math.PI / 180d;
        return (Math.Sin(radians), Math.Cos(radians));
    }

    /// <summary>
    /// Rotates a local offset - x right, y forward - onto map axes for a compass heading,
    /// so a symbol defined nose-up can be drawn pointing anywhere.
    /// </summary>
    public static (double X, double Y) RotateByHeading(double x, double y, double headingDegrees)
    {
        var radians = headingDegrees * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return ((x * cos) + (y * sin), (y * cos) - (x * sin));
    }

    /// <summary>Bearing from one map-space point to another, in compass degrees.</summary>
    public static double HeadingBetween(MPoint from, MPoint to)
    {
        var degrees = Math.Atan2(to.X - from.X, to.Y - from.Y) * 180d / Math.PI;
        return (degrees + 360d) % 360d;
    }

    /// <summary>
    /// Map units for a screen size at the current zoom. Symbols sized this way stay the same
    /// on screen instead of growing and shrinking with the viewport.
    /// </summary>
    public static double PixelsToMapUnits(double resolution, double pixels) => resolution * pixels;

    /// <summary>
    /// Metres converted to map units at a given latitude. Mercator stretches away from the
    /// equator, so a fence radius in metres is not a constant number of map units.
    /// </summary>
    public static double MetersToMapUnits(double meters, double latitude) =>
        meters / Math.Cos(latitude * Math.PI / 180d);

    public static double Lerp(double current, double target, double amount) => current + ((target - current) * amount);

    /// <summary>Interpolates a compass angle the short way around, so 350 -> 10 goes forward.</summary>
    public static double LerpAngle(double current, double target, double amount)
    {
        var difference = ((target - current + 540d) % 360d) - 180d;
        return (current + (difference * amount) + 360d) % 360d;
    }
}
