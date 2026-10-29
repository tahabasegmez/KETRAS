using Ketras.Model.Geo;

namespace Ketras.Tests.Geo;

public sealed class WebMercatorTests
{
    [Fact]
    public void Coordinates_round_trip_through_the_projection()
    {
        Assert.Equal(0.5, WebMercatorRules.X(0), 12);
        Assert.Equal(0.5, WebMercatorRules.Y(0), 12);
        Assert.Equal(47.397971, WebMercatorRules.Latitude(WebMercatorRules.Y(47.397971)), 9);
        Assert.Equal(8.546164, WebMercatorRules.Longitude(WebMercatorRules.X(8.546164)), 9);
        Assert.Equal(0, WebMercatorRules.Y(90), 6);
    }

    [Fact]
    public void A_box_maps_to_the_slippy_map_tiles_that_cover_it()
    {
        // Zurich at zoom 13 is tile 4290/2868 (OpenStreetMap numbering, y from the north).
        var range = WebMercatorRules.Tiles(47.37, 8.53, 47.38, 8.55, 13);

        Assert.Equal(new TileRange(13, 4290, 2868, 1, 1), range);
        Assert.InRange(47.38, WebMercatorRules.Latitude((range.MinY + 1) / 8192.0), WebMercatorRules.Latitude(range.MinY / 8192.0));
        Assert.Equal(new TileRange(0, 0, 0, 1, 1), WebMercatorRules.Tiles(-10, -10, 10, 10, 0));
    }

    [Fact]
    public void The_zoom_is_the_finest_that_fits_the_pixel_and_tile_caps()
    {
        // About 9 × 9 km: 16 would need over 4096 px (22 columns), 15 fits.
        var range = WebMercatorRules.PickTiles(47.36, 8.49, 47.44, 8.61);

        Assert.Equal(15, range.Zoom);
        Assert.True(Math.Max(range.Columns, range.Rows) * WebMercatorRules.TilePixels <= 4096);
        Assert.True(range.Count <= 256);
        Assert.Equal(17, WebMercatorRules.PickTiles(47.397, 8.546, 47.398, 8.547).Zoom);
        Assert.True(WebMercatorRules.PickTiles(47.36, 8.49, 47.44, 8.61, maxTiles: 4).Count <= 4);
    }

    [Fact]
    public void Lower_cameras_get_finer_zooms_up_to_the_cap()
    {
        int Zoom(double height) => WebMercatorRules.ZoomForHeight(height, 47.4, 50, 1000, 19);

        // At 1000 m with a 50° view 1000 px tall a screen pixel covers about 0.93 m: zoom 16 (1.6 m at 47.4°) is the
        // finest whose pixels are not smaller.
        Assert.Equal(16, Zoom(1000));
        Assert.Equal(17, Zoom(500));
        Assert.Equal(13, Zoom(8000));
        Assert.Equal(19, Zoom(10));
        Assert.True(Zoom(2000) < Zoom(1000));
    }

    [Fact]
    public void A_block_around_a_point_is_centred_on_its_tile_and_kept_on_the_map()
    {
        var block = WebMercatorRules.Around(47.375, 8.54, 13, 12);

        Assert.Equal(new TileRange(13, 4290 - 6, 2868 - 6, 12, 12), block);
        Assert.Equal(new TileRange(2, 0, 0, 4, 4), WebMercatorRules.Around(80, -170, 2, 12));
        Assert.Equal(0, WebMercatorRules.Around(85.05, -179, 15, 12).MinY);
    }
}
