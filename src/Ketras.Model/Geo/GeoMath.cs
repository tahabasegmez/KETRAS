namespace Ketras.Model.Geo;

/// <summary>Great-circle calculations on a spherical earth; the one place that knows the earth radius.</summary>
public static class GeoMath
{
    /// <summary>Mean earth radius used throughout.</summary>
    public const double EarthRadiusMeters = 6_371_000;

    /// <summary>Great-circle distance in metres.</summary>
    public static double DistanceMeters(GeoPoint from, GeoPoint to)
    {
        var lat1 = ToRadians(from.Latitude);
        var lat2 = ToRadians(to.Latitude);
        var deltaLat = lat2 - lat1;
        var deltaLon = ToRadians(to.Longitude - from.Longitude);

        var a = (Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)) +
                (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2));

        return 2 * EarthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Initial bearing from <paramref name="from"/> to <paramref name="to"/>, 0…360 degrees.</summary>
    public static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        var lat1 = ToRadians(from.Latitude);
        var lat2 = ToRadians(to.Latitude);
        var deltaLon = ToRadians(to.Longitude - from.Longitude);

        var y = Math.Sin(deltaLon) * Math.Cos(lat2);
        var x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon));

        return (ToDegrees(Math.Atan2(y, x)) + 360) % 360;
    }

    /// <summary>Point reached by travelling <paramref name="distanceMeters"/> along a bearing.</summary>
    public static GeoPoint Offset(GeoPoint from, double bearingDegrees, double distanceMeters)
    {
        var angular = distanceMeters / EarthRadiusMeters;
        var bearing = ToRadians(bearingDegrees);
        var lat1 = ToRadians(from.Latitude);
        var lon1 = ToRadians(from.Longitude);

        var lat2 = Math.Asin((Math.Sin(lat1) * Math.Cos(angular)) +
                             (Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing)));

        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1),
            Math.Cos(angular) - (Math.Sin(lat1) * Math.Sin(lat2)));

        return new GeoPoint(ToDegrees(lat2), ToDegrees(lon2));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double ToDegrees(double radians) => radians * 180d / Math.PI;
}
