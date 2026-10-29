using System.Net;

namespace Ketras.Terrain;

/// <summary>Outcome of <see cref="TerrainTileStore.EnsureAsync"/>, by cell label (e.g. <c>N47E008</c>).</summary>
/// <param name="Present">Already on disk.</param>
/// <param name="Downloaded">Fetched now.</param>
/// <param name="NotPublished">No file exists for the cell (HTTP 404): open water or a country not in this release.</param>
/// <param name="Failed">Could not be fetched (network, timeout, too large an area), with the reason.</param>
public sealed record TileFetchResult(
    IReadOnlyList<string> Present,
    IReadOnlyList<string> Downloaded,
    IReadOnlyList<string> NotPublished,
    IReadOnlyList<(string Cell, string Reason)> Failed)
{
    /// <summary>True when every cell has data on disk.</summary>
    public bool IsComplete => NotPublished.Count == 0 && Failed.Count == 0;
}

/// <summary>A grid read from disk and the cells that had no file.</summary>
/// <param name="Grid">Heights; NaN where a cell was missing.</param>
/// <param name="MissingCells">Cells without a file on disk (never fetched, or not published).</param>
public sealed record TerrainLoadResult(ElevationGrid Grid, IReadOnlyList<string> MissingCells);

/// <summary>
/// Repository of Copernicus DEM cell files in a local directory. <see cref="EnsureAsync"/> downloads
/// missing cells; <see cref="Load"/> only reads what is on disk, so checks work offline. A cell the
/// bucket does not have is remembered with a <c>.missing</c> marker and not requested again.
/// </summary>
public sealed class TerrainTileStore
{
    /// <summary>Largest number of cells one request may cover (about 40 MB each).</summary>
    public const int MaxCellsPerRequest = 16;

    private readonly HttpClient _http;

    /// <summary>Creates a store over <paramref name="cacheDirectory"/> (created if needed).</summary>
    public TerrainTileStore(string cacheDirectory, HttpClient? http = null)
    {
        CacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Directory holding the cell files.</summary>
    public string CacheDirectory { get; }

    /// <summary>Default location: <c>%LOCALAPPDATA%\Ketras\terrain</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.DoNotVerify), "Ketras", "terrain");

    /// <summary>Path of a cell's height file.</summary>
    public string TilePath(int latitude, int longitude) =>
        Path.Combine(CacheDirectory, CopernicusDem.ObjectName(latitude, longitude) + ".tif");

    private string MissingMarkerPath(int latitude, int longitude) =>
        Path.Combine(CacheDirectory, CopernicusDem.ObjectName(latitude, longitude) + ".missing");

    /// <summary>Downloads every cell of <paramref name="bounds"/> that is not on disk yet.</summary>
    public async Task<TileFetchResult> EnsureAsync(GeoBounds bounds, CancellationToken cancellation = default)
    {
        var present = new List<string>();
        var downloaded = new List<string>();
        var notPublished = new List<string>();
        var failed = new List<(string, string)>();
        var cells = CopernicusDem.CellsFor(bounds);
        if (cells.Count > MaxCellsPerRequest)
        {
            failed.Add(($"{cells.Count} cells", $"area too large (more than {MaxCellsPerRequest} cells)"));
            return new TileFetchResult(present, downloaded, notPublished, failed);
        }

        foreach (var (lat, lon) in cells)
        {
            var label = CopernicusDem.CellLabel(lat, lon);
            if (File.Exists(TilePath(lat, lon)))
            {
                present.Add(label);
                continue;
            }

            if (File.Exists(MissingMarkerPath(lat, lon)))
            {
                notPublished.Add(label);
                continue;
            }

            try
            {
                if (await DownloadAsync(lat, lon, cancellation))
                {
                    downloaded.Add(label);
                }
                else
                {
                    notPublished.Add(label);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                failed.Add((label, exception is OperationCanceledException ? "timed out" : exception.Message));
                if (cancellation.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        return new TileFetchResult(present, downloaded, notPublished, failed);
    }

    /// <summary>Labels of the cells of <paramref name="bounds"/> never fetched: no file and not known to be unpublished.</summary>
    public IReadOnlyList<string> AbsentCells(GeoBounds bounds) =>
        [.. CopernicusDem.CellsFor(bounds)
            .Where(cell => !File.Exists(TilePath(cell.Latitude, cell.Longitude)) && !File.Exists(MissingMarkerPath(cell.Latitude, cell.Longitude)))
            .Select(cell => CopernicusDem.CellLabel(cell.Latitude, cell.Longitude))];

    /// <summary>
    /// Reads the heights of <paramref name="bounds"/> from disk on the data's own 1 arc-second lattice,
    /// thinned by a whole factor when it would exceed <paramref name="maxPoints"/>. Never downloads.
    /// </summary>
    public TerrainLoadResult Load(GeoBounds bounds, int maxPoints = 4_000_000)
    {
        const double arcSecond = 1.0 / 3600;
        var cells = CopernicusDem.CellsFor(bounds);
        var readers = new Dictionary<(int, int), GeoTiffTileReader>();
        var missing = new List<string>();
        try
        {
            foreach (var (lat, lon) in cells)
            {
                var path = TilePath(lat, lon);
                if (File.Exists(path))
                {
                    readers[(lat, lon)] = GeoTiffTileReader.Open(path);
                }
                else
                {
                    missing.Add(CopernicusDem.CellLabel(lat, lon));
                }
            }

            // Above 50° latitude the files use wider longitude spacing; the widest one sets the lattice.
            var lonSpacing = readers.Count > 0 ? readers.Values.Max(r => r.LongitudeStep) : arcSecond;
            var rows0 = ((bounds.North - bounds.South) / arcSecond) + 1;
            var cols0 = ((bounds.East - bounds.West) / lonSpacing) + 1;
            var factor = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(rows0 * cols0 / maxPoints)));
            var latStep = arcSecond * factor;
            var lonStep = lonSpacing * factor;

            var south = Math.Floor(bounds.South / latStep) * latStep;
            var west = Math.Floor(bounds.West / lonStep) * lonStep;
            var rows = (int)Math.Ceiling((bounds.North - south) / latStep) + 1;
            var cols = (int)Math.Ceiling((bounds.East - west) / lonStep) + 1;
            var heights = new float[rows * cols];

            for (var r = 0; r < rows; r++)
            {
                var lat = south + (r * latStep);
                var cellLat = CopernicusDem.CellLatitude(lat);
                for (var c = 0; c < cols; c++)
                {
                    var lon = west + (c * lonStep);
                    heights[(r * cols) + c] = readers.TryGetValue((cellLat, CopernicusDem.CellLongitude(lon)), out var reader)
                        ? reader.NearestHeight(lat, lon) ?? float.NaN
                        : float.NaN;
                }
            }

            return new TerrainLoadResult(new ElevationGrid(south, west, latStep, lonStep, rows, cols, heights, CopernicusDem.Attribution), missing);
        }
        finally
        {
            foreach (var reader in readers.Values)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>True when downloaded, false when the bucket has no such cell (404).</summary>
    private async Task<bool> DownloadAsync(int lat, int lon, CancellationToken cancellation)
    {
        using var response = await _http.GetAsync(CopernicusDem.TileUrl(lat, lon), HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await File.WriteAllTextAsync(MissingMarkerPath(lat, lon), "HTTP 404", CancellationToken.None);
            return false;
        }

        response.EnsureSuccessStatusCode();
        var target = TilePath(lat, lon);
        var part = target + ".part";
        await using (var file = File.Create(part))
        {
            await response.Content.CopyToAsync(file, cancellation);
        }

        File.Move(part, target, overwrite: true);
        return true;
    }
}
