namespace Ketras.Model.Geo;

/// <summary>
/// A block of XYZ map tiles at one zoom, the OpenStreetMap tile scheme (y = 0 at the north edge).
/// </summary>
/// <param name="Zoom">Zoom level.</param>
/// <param name="MinX">Westmost column.</param>
/// <param name="MinY">Northmost row.</param>
/// <param name="Columns">Number of columns.</param>
/// <param name="Rows">Number of rows.</param>
public readonly record struct TileRange(int Zoom, int MinX, int MinY, int Columns, int Rows)
{
    /// <summary>Number of tiles in the block.</summary>
    public int Count => Columns * Rows;
}

/// <summary>Pure rules: spherical web mercator and the XYZ tile scheme that slippy-map tile servers use.</summary>
public static class WebMercatorRules
{
    /// <summary>Edge length of one tile in pixels.</summary>
    public const int TilePixels = 256;

    /// <summary>Latitude limit of the projection, degrees.</summary>
    public const double MaxLatitude = 85.05112878;

    /// <summary>Longitude to the mercator x fraction: 0 at 180° W, 1 at 180° E.</summary>
    public static double X(double longitude) => (longitude + 180) / 360;

    /// <summary>Latitude to the mercator y fraction: 0 at the north limit, 1 at the south limit.</summary>
    public static double Y(double latitude)
    {
        var phi = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        return (1 - (Math.Log(Math.Tan(phi) + (1 / Math.Cos(phi))) / Math.PI)) / 2;
    }

    /// <summary>The mercator x fraction back to longitude.</summary>
    public static double Longitude(double x) => (x * 360) - 180;

    /// <summary>The mercator y fraction back to latitude.</summary>
    public static double Latitude(double y) => Math.Atan(Math.Sinh(Math.PI * (1 - (2 * y)))) * 180 / Math.PI;

    /// <summary>The tiles covering a box at <paramref name="zoom"/>.</summary>
    public static TileRange Tiles(double south, double west, double north, double east, int zoom)
    {
        var n = 1 << zoom;
        var minX = Math.Clamp((int)Math.Floor(X(west) * n), 0, n - 1);
        var maxX = Math.Clamp((int)Math.Floor(X(east) * n), 0, n - 1);
        var minY = Math.Clamp((int)Math.Floor(Y(north) * n), 0, n - 1);
        var maxY = Math.Clamp((int)Math.Floor(Y(south) * n), 0, n - 1);
        return new TileRange(zoom, minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// The finest tile block over a box that stays within <paramref name="maxPixels"/> on its longer side and
    /// <paramref name="maxTiles"/> tiles, at <paramref name="maxZoom"/> at most; zoom 0 when nothing finer fits.
    /// </summary>
    public static TileRange PickTiles(double south, double west, double north, double east, int maxZoom = 17, int maxPixels = 4096, int maxTiles = 256)
    {
        var best = Tiles(south, west, north, east, 0);
        for (var zoom = 1; zoom <= maxZoom; zoom++)
        {
            var range = Tiles(south, west, north, east, zoom);
            if (Math.Max(range.Columns, range.Rows) * TilePixels > maxPixels || range.Count > maxTiles)
            {
                break;
            }

            best = range;
        }

        return best;
    }
}
