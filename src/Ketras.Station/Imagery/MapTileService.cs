using System.Net;
using Ketras.Model.Geo;
using Ketras.Station.Settings;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Imagery;

/// <summary>One encoded tile image (PNG or JPEG, as the server sent it).</summary>
/// <param name="X">Column.</param>
/// <param name="Y">Row, 0 at the north.</param>
/// <param name="Encoded">The image file's bytes.</param>
public sealed record ImageryTile(int X, int Y, byte[] Encoded);

/// <summary>A block of map tiles over an area; tiles that could not be had are left out.</summary>
/// <param name="Range">The block.</param>
/// <param name="Tiles">The tiles that were had.</param>
/// <param name="Attribution">The source's credit.</param>
public sealed record ImageryMosaic(TileRange Range, IReadOnlyList<ImageryTile> Tiles, string Attribution);

/// <summary>
/// Fetches map tiles over an area for the 3D scene: one zoom fine enough for a texture of at most 4096 px a side, each
/// tile read from the disk cache the planning map fills (<c>{tiles}/{source}/{z}/{x}/{y}.tile</c>, BruTile's
/// <c>FileCache</c> layout) or downloaded into it. A tile that fails is left out. No bulk download beyond the one area
/// (the OpenStreetMap tile usage policy). Thread-safe.
/// </summary>
public sealed class MapTileService : IDisposable
{
    /// <summary>Tile servers ask clients to identify themselves; the same agent as the planning map.</summary>
    public const string UserAgent = "Ketras ground station (https://github.com/ketras)";

    /// <summary>Finest zoom fetched for the scene.</summary>
    public const int MaxZoom = 17;

    private const int Parallel = 4;
    private static readonly TimeSpan TileTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly ILogger<MapTileService> _logger;

    /// <summary>Caches under the station's <c>tiles</c> data directory.</summary>
    public MapTileService(StationPaths paths, ILogger<MapTileService> logger)
        : this(paths.DataPath(null, "tiles"), logger, new SocketsHttpHandler())
    {
    }

    internal MapTileService(string cacheDirectory, ILogger<MapTileService> logger, HttpMessageHandler handler)
    {
        CacheDirectory = cacheDirectory;
        _logger = logger;
        _http = new HttpClient(handler) { Timeout = TileTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>The cache root; each source has a folder under it.</summary>
    public string CacheDirectory { get; }

    /// <summary>The cache file of one tile.</summary>
    public string TilePath(TileSource source, int zoom, int x, int y) =>
        Path.Combine(CacheDirectory, source.Name, Invariant(zoom), Invariant(x), Invariant(y) + ".tile");

    /// <summary>
    /// The tiles of <paramref name="source"/> over a box. Throws only on cancellation; a box without any tile gives an
    /// empty mosaic.
    /// </summary>
    public async Task<ImageryMosaic> LoadAsync(TileSource source, double south, double west, double north, double east, CancellationToken cancellation = default)
    {
        var range = WebMercatorRules.PickTiles(south, west, north, east, Math.Min(MaxZoom, source.MaxZoom));
        var cells = new List<(int X, int Y)>(range.Count);
        for (var row = 0; row < range.Rows; row++)
        {
            for (var column = 0; column < range.Columns; column++)
            {
                cells.Add((range.MinX + column, range.MinY + row));
            }
        }

        var tiles = new ImageryTile?[cells.Count];
        await System.Threading.Tasks.Parallel.ForEachAsync(
            Enumerable.Range(0, cells.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancellation },
            async (index, token) =>
            {
                var (x, y) = cells[index];
                tiles[index] = await TileAsync(source, range.Zoom, x, y, token);
            });

        return new ImageryMosaic(range, [.. tiles.OfType<ImageryTile>()], source.Attribution);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<ImageryTile?> TileAsync(TileSource source, int zoom, int x, int y, CancellationToken cancellation)
    {
        var path = TilePath(source, zoom, x, y);
        try
        {
            if (File.Exists(path))
            {
                return new ImageryTile(x, y, await File.ReadAllBytesAsync(path, cancellation));
            }

            using var response = await _http.GetAsync(source.TileUrl(zoom, x, y), cancellation);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogDebug("Tile {Source} {Zoom}/{X}/{Y}: HTTP {Status}", source.Name, zoom, x, y, (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".part";
            await File.WriteAllBytesAsync(temporary, bytes, cancellation);
            File.Move(temporary, path, overwrite: true);
            return new ImageryTile(x, y, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       || (ex is TaskCanceledException && !cancellation.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "Tile {Source} {Zoom}/{X}/{Y} not loaded", source.Name, zoom, x, y);
            return null;
        }
    }

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
