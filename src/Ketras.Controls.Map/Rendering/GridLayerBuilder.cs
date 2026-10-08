using Mapsui;
using Mapsui.Nts;
using NetTopologySuite.Geometries;

namespace Ketras.Controls.Map.Rendering;

/// <summary>
/// Builder: the metric grid drawn on the plain background when the base map is off. The spacing is the
/// smallest round distance that stays at least <see cref="MinimumPixels"/> apart on screen; every fifth
/// line is a major one.
/// </summary>
internal static class GridLayerBuilder
{
    private const double MinimumPixels = 40;
    private const int MajorEvery = 5;

    private static readonly double[] SpacingsMeters =
    [
        10, 20, 50, 100, 200, 500, 1_000, 2_000, 5_000, 10_000, 20_000, 50_000, 100_000, 200_000, 500_000, 1_000_000
    ];

    public static IReadOnlyList<IFeature> Build(MRect extent, double resolution)
    {
        if (resolution <= 0 || extent.Width <= 0 || extent.Height <= 0)
        {
            return [];
        }

        // Web Mercator stretches ground distance by 1 / cos(latitude); the grid is metric at the view centre.
        var latitude = MapGeometry.ToWgs84(new MPoint(extent.Centroid.X, extent.Centroid.Y)).Latitude;
        var mapUnitsPerMeter = 1 / Math.Cos(latitude * Math.PI / 180d);
        var spacing = SpacingsMeters.Select(meters => meters * mapUnitsPerMeter).FirstOrDefault(units => units / resolution >= MinimumPixels);
        if (spacing <= 0)
        {
            return [];
        }

        var features = new List<IFeature>();
        AddLines(features, extent.MinX, extent.MaxX, spacing, x => new LineString([new Coordinate(x, extent.MinY), new Coordinate(x, extent.MaxY)]));
        AddLines(features, extent.MinY, extent.MaxY, spacing, y => new LineString([new Coordinate(extent.MinX, y), new Coordinate(extent.MaxX, y)]));
        return features;
    }

    private static void AddLines(List<IFeature> features, double from, double to, double spacing, Func<double, LineString> line)
    {
        for (var index = (long)Math.Ceiling(from / spacing); index * spacing <= to; index++)
        {
            var feature = new GeometryFeature(line(index * spacing));
            feature.Styles.Add(MapStyles.GridLine(index % MajorEvery == 0));
            features.Add(feature);
        }
    }
}
