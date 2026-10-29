using System.Net;
using Ketras.Terrain;

namespace Ketras.Tests.Terrain;

public sealed class TerrainTileTests : IDisposable
{
    private const double ArcSecond = 1.0 / 3600;
    private readonly string _directory = Directory.CreateTempSubdirectory("ketras-terrain-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(47, 8, "N47E008", "Copernicus_DSM_COG_10_N47_00_E008_00_DEM")]
    [InlineData(-34, -71, "S34W071", "Copernicus_DSM_COG_10_S34_00_W071_00_DEM")]
    public void Cells_are_named_like_the_bucket(int latitude, int longitude, string label, string objectName)
    {
        Assert.Equal(label, CopernicusDem.CellLabel(latitude, longitude));
        Assert.Equal(objectName, CopernicusDem.ObjectName(latitude, longitude));
        Assert.EndsWith($"/{objectName}/{objectName}.tif", CopernicusDem.TileUrl(latitude, longitude).AbsoluteUri);
    }

    [Fact]
    public void A_cells_north_edge_belongs_to_it_and_its_south_edge_to_the_cell_below()
    {
        Assert.Equal(46, CopernicusDem.CellLatitude(47.0));
        Assert.Equal(47, CopernicusDem.CellLatitude(47.4));
        Assert.Equal([(46, 8)], CopernicusDem.CellsFor(new GeoBounds(46.5, 8.2, 47.0, 8.4)));
        Assert.Equal(4, CopernicusDem.CellsFor(new GeoBounds(46.5, 7.5, 47.5, 8.5)).Count);
    }

    [Fact]
    public void Bounds_grow_by_metres_and_contain_their_points()
    {
        var box = GeoBounds.Around([(47.0, 8.0), (47.1, 8.2)])!.Value;
        var grown = box.Expand(1000);

        Assert.Equal(47.0 - (1000 / 111_195.0), grown.South, 9);
        Assert.True(grown.Contains(47.05, 8.1));
        Assert.False(box.Contains(46.99, 8.1));
        Assert.Null(GeoBounds.Around([]));
    }

    [Fact]
    public void The_grid_interpolates_bilinearly_and_has_no_height_next_to_missing_data()
    {
        var grid = new ElevationGrid(10, 20, 1, 1, 2, 3, [0, 10, 20, 100, 110, float.NaN], "test");

        Assert.Equal(55, grid.HeightAt(10.5, 20.5)!.Value, 9);
        Assert.Equal(5, grid.HeightAt(10, 20.5)!.Value, 9);
        Assert.Null(grid.HeightAt(10.5, 21.5));
        Assert.Null(grid.HeightAt(9.9, 20));
    }

    [Fact]
    public void The_reader_decodes_tiled_float_deflate_with_the_predictor()
    {
        var path = Path.Combine(_directory, "synthetic.tif");
        SyntheticGeoTiff.Write(path, 40, 30, 16, 11, 20, ArcSecond, (r, c) => (r * 100) + c + 0.25f);

        using var reader = GeoTiffTileReader.Open(path);

        Assert.Equal(40, reader.Width);
        Assert.Equal(30, reader.Height);
        Assert.Equal(11, reader.OriginLatitude, 9);
        Assert.Equal(20, reader.OriginLongitude, 9);
        Assert.Equal(0.25f, reader.Pixel(0, 0));
        Assert.Equal(2039.25f, reader.Pixel(20, 39));
        Assert.Equal(1517.25f, reader.Pixel(15, 17));
        Assert.True(float.IsNaN(reader.Pixel(30, 0)));
        Assert.Equal(1517.25, reader.HeightAt(11 - (15 * ArcSecond), 20 + (17 * ArcSecond))!.Value, 3);
        Assert.Equal(1567.75, reader.HeightAt(11 - (15.5 * ArcSecond), 20 + (17.5 * ArcSecond))!.Value, 3);
    }

    [Fact]
    public void A_file_that_is_not_a_classic_little_endian_tiff_is_rejected()
    {
        var path = Path.Combine(_directory, "bad.tif");
        File.WriteAllBytes(path, "MM\0*\0\0\0\b"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => GeoTiffTileReader.Open(path));
    }

    [Fact]
    public void Load_reads_the_cells_on_disk_and_reports_the_missing_ones()
    {
        var store = new TerrainTileStore(_directory);
        SyntheticGeoTiff.Write(store.TilePath(10, 20), 64, 64, 32, 11, 20, ArcSecond, (r, c) => 500 + r);

        var load = store.Load(new GeoBounds(10.99, 20.001, 10.995, 20.003));

        Assert.Empty(load.MissingCells);
        var expected = 500 + Math.Round((11 - 10.993) / ArcSecond);
        Assert.Equal(expected, load.Grid.HeightAt(10.993, 20.002)!.Value, 0);

        var outside = store.Load(new GeoBounds(10.99, 20.5, 10.995, 21.2));
        Assert.Equal(["N10E021"], outside.MissingCells);
    }

    [Fact]
    public async Task Ensure_downloads_once_and_remembers_cells_the_bucket_does_not_publish()
    {
        var handler = new FakeBucket();
        handler.Files[CopernicusDem.TileUrl(47, 8)] = [1, 2, 3];
        var store = new TerrainTileStore(_directory, new HttpClient(handler));
        var area = new GeoBounds(47.2, 7.5, 47.3, 8.5);

        var first = await store.EnsureAsync(area);
        var second = await store.EnsureAsync(area);

        Assert.Equal(["N47E008"], first.Downloaded);
        Assert.Equal(["N47E007"], first.NotPublished);
        Assert.Equal(["N47E008"], second.Present);
        Assert.Equal(["N47E007"], second.NotPublished);
        Assert.Equal(2, handler.Requests);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(store.TilePath(47, 8)));
    }

    [Fact]
    public async Task Ensure_refuses_an_area_with_too_many_cells()
    {
        var store = new TerrainTileStore(_directory, new HttpClient(new FakeBucket()));

        var result = await store.EnsureAsync(new GeoBounds(40, 0, 45, 5));

        Assert.False(result.IsComplete);
        Assert.Contains("too large", Assert.Single(result.Failed).Reason);
    }

    private sealed class FakeBucket : HttpMessageHandler
    {
        public Dictionary<Uri, byte[]> Files { get; } = [];

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Files.TryGetValue(request.RequestUri!, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
