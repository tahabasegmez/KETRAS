namespace Ketras.Geo;

/// <summary>A WGS84 position in degrees.</summary>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    /// <summary>True for (0, 0), which the model uses for "not set" (e.g. a planned home not known yet).</summary>
    public bool IsUnset => Latitude == 0 && Longitude == 0;
}
