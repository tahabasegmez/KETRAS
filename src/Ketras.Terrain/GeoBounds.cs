namespace Ketras.Terrain;

/// <summary>A latitude/longitude box in WGS84 degrees. Does not cross the antimeridian.</summary>
/// <param name="South">Southern edge.</param>
/// <param name="West">Western edge.</param>
/// <param name="North">Northern edge.</param>
/// <param name="East">Eastern edge.</param>
public readonly record struct GeoBounds(double South, double West, double North, double East)
{
    private const double MetersPerDegreeLatitude = 111_195.0;

    /// <summary>The smallest box around the given points; null when there are none.</summary>
    public static GeoBounds? Around(IEnumerable<(double Latitude, double Longitude)> points)
    {
        GeoBounds? box = null;
        foreach (var (lat, lon) in points)
        {
            box = box is { } b
                ? new GeoBounds(Math.Min(b.South, lat), Math.Min(b.West, lon), Math.Max(b.North, lat), Math.Max(b.East, lon))
                : new GeoBounds(lat, lon, lat, lon);
        }

        return box;
    }

    /// <summary>This box grown by <paramref name="meters"/> on every side.</summary>
    public GeoBounds Expand(double meters)
    {
        var dLat = meters / MetersPerDegreeLatitude;
        var cos = Math.Max(Math.Cos((South + North) / 2 * Math.PI / 180), 0.01);
        var dLon = meters / (MetersPerDegreeLatitude * cos);
        return new GeoBounds(Math.Max(South - dLat, -90), Math.Max(West - dLon, -180), Math.Min(North + dLat, 90), Math.Min(East + dLon, 180));
    }

    /// <summary>True when the point lies inside or on the edge.</summary>
    public bool Contains(double latitude, double longitude) =>
        latitude >= South && latitude <= North && longitude >= West && longitude <= East;
}
