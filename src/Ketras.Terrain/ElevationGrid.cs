namespace Ketras.Terrain;

/// <summary>
/// Regular latitude/longitude grid of terrain heights in metres above mean sea level (EGM2008 for
/// Copernicus data). Row 0 is the southern row; NaN marks a point without data.
/// </summary>
public sealed class ElevationGrid
{
    private readonly float[] _heights;

    /// <summary>Creates a grid; <paramref name="heights"/> is row-major, south row first.</summary>
    public ElevationGrid(double southLatitude, double westLongitude, double latitudeStep, double longitudeStep, int rows, int columns, float[] heights, string attribution)
    {
        if (heights.Length != rows * columns)
        {
            throw new ArgumentException($"expected {rows * columns} heights, got {heights.Length}", nameof(heights));
        }

        SouthLatitude = southLatitude;
        WestLongitude = westLongitude;
        LatitudeStep = latitudeStep;
        LongitudeStep = longitudeStep;
        Rows = rows;
        Columns = columns;
        _heights = heights;
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

    /// <summary>Heights, row-major, south row first; NaN where there is no data.</summary>
    public ReadOnlySpan<float> Heights => _heights;

    /// <summary>Attribution the data licence requires to be shown with anything derived from it.</summary>
    public string Attribution { get; }

    /// <summary>Copy of the heights array.</summary>
    public float[] ToArray() => (float[])_heights.Clone();

    /// <summary>Bilinear height at a point; null outside the grid or next to a point without data.</summary>
    public double? HeightAt(double latitude, double longitude)
    {
        var y = (latitude - SouthLatitude) / LatitudeStep;
        var x = (longitude - WestLongitude) / LongitudeStep;
        if (y < 0 || x < 0 || y > Rows - 1 || x > Columns - 1)
        {
            return null;
        }

        var r0 = Math.Min((int)y, Rows - 2 < 0 ? 0 : Rows - 2);
        var c0 = Math.Min((int)x, Columns - 2 < 0 ? 0 : Columns - 2);
        var r1 = Math.Min(r0 + 1, Rows - 1);
        var c1 = Math.Min(c0 + 1, Columns - 1);
        var fy = y - r0;
        var fx = x - c0;

        double h00 = _heights[(r0 * Columns) + c0], h01 = _heights[(r0 * Columns) + c1];
        double h10 = _heights[(r1 * Columns) + c0], h11 = _heights[(r1 * Columns) + c1];
        if (double.IsNaN(h00) || double.IsNaN(h01) || double.IsNaN(h10) || double.IsNaN(h11))
        {
            return null;
        }

        return ((h00 * (1 - fx)) + (h01 * fx)) * (1 - fy) + ((h10 * (1 - fx)) + (h11 * fx)) * fy;
    }
}
