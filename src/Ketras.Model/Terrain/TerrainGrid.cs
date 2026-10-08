using Ketras.Model.Geo;

namespace Ketras.Model.Terrain;

/// <summary>
/// Terrain heights on a regular latitude/longitude grid, south row first, in metres above mean sea level; NaN marks a
/// point without data. Immutable.
/// </summary>
public sealed class TerrainGrid
{
    private readonly float[] _heights;

    /// <summary>Creates a grid; <paramref name="heights"/> is row-major, south row first, and is copied.</summary>
    public TerrainGrid(double southLatitude, double westLongitude, double latitudeStep, double longitudeStep,
        int rows, int columns, ReadOnlySpan<float> heights, string? attribution = null)
    {
        if (rows < 2 || columns < 2 || heights.Length != rows * columns || latitudeStep <= 0 || longitudeStep <= 0)
        {
            throw new ArgumentException("A terrain grid needs at least 2×2 points, positive steps and rows × columns heights.");
        }

        SouthLatitude = southLatitude;
        WestLongitude = westLongitude;
        LatitudeStep = latitudeStep;
        LongitudeStep = longitudeStep;
        Rows = rows;
        Columns = columns;
        _heights = heights.ToArray();
        Attribution = attribution;
    }

    /// <summary>Latitude of row 0.</summary>
    public double SouthLatitude { get; }

    /// <summary>Longitude of column 0.</summary>
    public double WestLongitude { get; }

    /// <summary>Degrees between rows.</summary>
    public double LatitudeStep { get; }

    /// <summary>Degrees between columns.</summary>
    public double LongitudeStep { get; }

    /// <summary>Number of rows.</summary>
    public int Rows { get; }

    /// <summary>Number of columns.</summary>
    public int Columns { get; }

    /// <summary>Source credit that must be shown with anything derived from the data.</summary>
    public string? Attribution { get; }

    /// <summary>Northern edge.</summary>
    public double NorthLatitude => SouthLatitude + ((Rows - 1) * LatitudeStep);

    /// <summary>Eastern edge.</summary>
    public double EastLongitude => WestLongitude + ((Columns - 1) * LongitudeStep);

    /// <summary>Height at a grid point; NaN without data.</summary>
    public float Height(int row, int column) => _heights[(row * Columns) + column];

    /// <summary>All heights, row-major, south row first.</summary>
    public ReadOnlySpan<float> Heights => _heights;

    /// <summary>Bilinear height at a point; null outside the grid or next to a point without data.</summary>
    public double? HeightAt(GeoPoint point)
    {
        var y = (point.Latitude - SouthLatitude) / LatitudeStep;
        var x = (point.Longitude - WestLongitude) / LongitudeStep;
        if (y < 0 || x < 0 || y > Rows - 1 || x > Columns - 1)
        {
            return null;
        }

        var r0 = Math.Min((int)y, Rows - 2);
        var c0 = Math.Min((int)x, Columns - 2);
        var fy = y - r0;
        var fx = x - c0;
        double h00 = Height(r0, c0), h01 = Height(r0, c0 + 1);
        double h10 = Height(r0 + 1, c0), h11 = Height(r0 + 1, c0 + 1);
        if (double.IsNaN(h00) || double.IsNaN(h01) || double.IsNaN(h10) || double.IsNaN(h11))
        {
            return null;
        }

        return ((h00 * (1 - fx)) + (h01 * fx)) * (1 - fy) + ((h10 * (1 - fx)) + (h11 * fx)) * fy;
    }
}
