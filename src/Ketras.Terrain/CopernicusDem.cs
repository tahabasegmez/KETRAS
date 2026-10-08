using System.Globalization;

namespace Ketras.Terrain;

/// <summary>
/// Copernicus DEM GLO-30 (1 arc-second, EGM2008 heights, a surface model including buildings and
/// vegetation) as published in the AWS Open Data bucket <c>copernicus-dem-30m</c>: one GeoTIFF per
/// 1°×1° cell. Naming and licence facts are in docs/reference/terrain.md.
/// </summary>
public static class CopernicusDem
{
    /// <summary>Public bucket base address (anonymous HTTPS).</summary>
    public const string BucketUrl = "https://copernicus-dem-30m.s3.amazonaws.com";

    /// <summary>Attribution the licence requires for products derived from the data.</summary>
    public const string Attribution =
        "produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved";

    /// <summary>Short cell label, e.g. <c>N47E008</c>, for the cell whose south-west corner is (lat, lon).</summary>
    public static string CellLabel(int latitude, int longitude) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(latitude >= 0 ? 'N' : 'S')}{Math.Abs(latitude):00}{(longitude >= 0 ? 'E' : 'W')}{Math.Abs(longitude):000}");

    /// <summary>Bucket object name without extension, e.g. <c>Copernicus_DSM_COG_10_N47_00_E008_00_DEM</c>.</summary>
    public static string ObjectName(int latitude, int longitude) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Copernicus_DSM_COG_10_{(latitude >= 0 ? 'N' : 'S')}{Math.Abs(latitude):00}_00_{(longitude >= 0 ? 'E' : 'W')}{Math.Abs(longitude):000}_00_DEM");

    /// <summary>Download address of the cell's height file.</summary>
    public static Uri TileUrl(int latitude, int longitude)
    {
        var name = ObjectName(latitude, longitude);
        return new Uri($"{BucketUrl}/{name}/{name}.tif");
    }

    /// <summary>
    /// Cells covering a box. A cell's file holds its north edge but not its south edge (that is the
    /// next cell's first row), so a box ending exactly on a whole degree does not pull in the cell above.
    /// </summary>
    public static IReadOnlyList<(int Latitude, int Longitude)> CellsFor(GeoBounds bounds)
    {
        var cells = new List<(int, int)>();
        var firstLat = CellLatitude(bounds.South);
        var lastLat = CellLatitude(bounds.North);
        var firstLon = (int)Math.Floor(bounds.West);
        var lastLon = (int)Math.Floor(bounds.East);
        for (var lat = firstLat; lat <= lastLat; lat++)
        {
            for (var lon = firstLon; lon <= lastLon; lon++)
            {
                cells.Add((lat, lon));
            }
        }

        return cells;
    }

    /// <summary>Cell (south-west corner latitude) whose file holds this latitude.</summary>
    public static int CellLatitude(double latitude) => (int)Math.Ceiling(latitude) - 1;

    /// <summary>Cell (south-west corner longitude) whose file holds this longitude.</summary>
    public static int CellLongitude(double longitude) => (int)Math.Floor(longitude);
}
