using System.Collections.Concurrent;
using System.Net;
using Ketras.Map;
using Ketras.Model.Geo;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.Tests.Map;

public sealed class ImageryTests : IDisposable
{
    // A box small enough to be one tile at the finest zoom.
    private const double South = 47.3975, West = 8.5460, North = 47.3976, East = 8.5461;

    private readonly string _directory = Directory.CreateTempSubdirectory("ketras-tiles-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Tiles_are_downloaded_once_into_the_planning_maps_cache_layout()
    {
        var range = WebMercatorRules.PickTiles(South, West, North, East, MapTileService.MaxZoom);
        var url = $"https://tiles.test/{range.Zoom}/{range.MinX}/{range.MinY}.png";
        var http = new TileServer(new Dictionary<string, byte[]> { [url] = [1, 2, 3] });
        using var service = new MapTileService(_directory, NullLogger<MapTileService>.Instance, http);
        var source = TileSource.Custom("https://tiles.test/{z}/{x}/{y}.png", "Test tiles");

        var first = await service.LoadAsync(source, South, West, North, East);
        var second = await service.LoadAsync(source, South, West, North, East);

        Assert.Equal((range, "Test tiles"), (first.Range, first.Attribution));
        Assert.Equal([1, 2, 3], Assert.Single(first.Tiles).Encoded);
        Assert.Equal([1, 2, 3], Assert.Single(second.Tiles).Encoded);
        Assert.Equal([url], http.Requested);
        Assert.True(File.Exists(Path.Combine(_directory, "custom", $"{range.Zoom}", $"{range.MinX}", $"{range.MinY}.tile")));
        Assert.Contains("Ketras", Assert.Single(http.UserAgents.Distinct()));
    }

    [Fact]
    public async Task A_tile_the_server_does_not_have_is_left_out()
    {
        var http = new TileServer(new Dictionary<string, byte[]>());
        using var service = new MapTileService(_directory, NullLogger<MapTileService>.Instance, http);

        var mosaic = await service.LoadAsync(TileSource.Street, South, West, North, East);

        Assert.Empty(mosaic.Tiles);
        Assert.StartsWith("https://tile.openstreetmap.org/17/", Assert.Single(http.Requested));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Satellite_urls_put_the_row_before_the_column_and_cancellation_stops_the_load()
    {
        var http = new TileServer(new Dictionary<string, byte[]>());
        using var service = new MapTileService(_directory, NullLogger<MapTileService>.Instance, http);
        var range = WebMercatorRules.PickTiles(South, West, North, East, MapTileService.MaxZoom);

        await service.LoadAsync(TileSource.Satellite, South, West, North, East);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.EndsWith($"/tile/{range.Zoom}/{range.MinY}/{range.MinX}", Assert.Single(http.Requested));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(TileSource.Satellite, South, West, North, East, cancelled.Token));
    }

    [Fact]
    public async Task A_given_block_loads_its_tiles_and_one_beyond_the_source_is_refused()
    {
        var http = new TileServer(new Dictionary<string, byte[]> { ["https://tiles.test/19/10/20.png"] = [7] });
        using var service = new MapTileService(_directory, NullLogger<MapTileService>.Instance, http);
        var source = TileSource.Custom("https://tiles.test/{z}/{x}/{y}.png", "Test tiles");

        var mosaic = await service.LoadAsync(source, new TileRange(19, 10, 20, 2, 1));

        Assert.Equal((10, 20, 19), (Assert.Single(mosaic.Tiles).X, mosaic.Tiles[0].Y, mosaic.MaxZoom));
        Assert.Equal(2, http.Requested.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LoadAsync(source with { MaxZoom = 18 }, new TileRange(19, 10, 20, 1, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LoadAsync(source, new TileRange(19, 0, 0, 17, 17)));
    }

    /// <summary>Serves fixed bodies by URL; records each request's URL and user agent.</summary>
    private sealed class TileServer(IReadOnlyDictionary<string, byte[]> bodies) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requested = new();
        private readonly ConcurrentQueue<string> _agents = new();

        public IReadOnlyList<string> Requested => [.. _requested];

        public IReadOnlyList<string> UserAgents => [.. _agents];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requested.Enqueue(request.RequestUri!.ToString());
            _agents.Enqueue(request.Headers.UserAgent.ToString());
            return Task.FromResult(bodies.TryGetValue(request.RequestUri.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
