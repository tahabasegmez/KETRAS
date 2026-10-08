using System.Collections.Immutable;

namespace Ketras.Model.Geo;

/// <summary>An area on the ground, without altitude: a circle or a polygon. Shared by fences and environment zones.</summary>
public abstract record Area
{
    /// <summary>True when <paramref name="point"/> lies inside (or on the edge of a circle).</summary>
    public abstract bool Contains(GeoPoint point);

    /// <summary>Points that bound the area: the centre for a circle (with <see cref="CircleArea.RadiusMeters"/>), the vertices for a polygon.</summary>
    public abstract IEnumerable<GeoPoint> Points { get; }
}

/// <summary>A circle on the ground.</summary>
/// <param name="Center">Centre.</param>
/// <param name="RadiusMeters">Radius in metres.</param>
public sealed record CircleArea(GeoPoint Center, double RadiusMeters) : Area
{
    /// <inheritdoc />
    public override bool Contains(GeoPoint point) => GeoMath.DistanceMeters(Center, point) <= RadiusMeters;

    /// <inheritdoc />
    public override IEnumerable<GeoPoint> Points => [Center];
}

/// <summary>A closed polygon on the ground; the last vertex connects back to the first.</summary>
/// <param name="Vertices">Vertices in order.</param>
public sealed record PolygonArea(ImmutableArray<GeoPoint> Vertices) : Area
{
    /// <summary>Even-odd ray cast on latitude/longitude; exact enough for fence-sized areas.</summary>
    public override bool Contains(GeoPoint point)
    {
        var inside = false;
        for (int i = 0, j = Vertices.Length - 1; i < Vertices.Length; j = i++)
        {
            var a = Vertices[i];
            var b = Vertices[j];
            if ((a.Latitude > point.Latitude) != (b.Latitude > point.Latitude) &&
                point.Longitude < ((b.Longitude - a.Longitude) * (point.Latitude - a.Latitude) / (b.Latitude - a.Latitude)) + a.Longitude)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <inheritdoc />
    public override IEnumerable<GeoPoint> Points => Vertices;

    /// <summary>Value equality over the vertices.</summary>
    public bool Equals(PolygonArea? other) => other is not null && Vertices.SequenceEqual(other.Vertices);

    /// <inheritdoc />
    public override int GetHashCode() => Vertices.Aggregate(0, (hash, vertex) => HashCode.Combine(hash, vertex));
}
