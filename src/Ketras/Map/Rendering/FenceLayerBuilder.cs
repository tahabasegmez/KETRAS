using Ketras.Geo;
using Ketras.Planning.Fences;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using NetTopologySuite.Geometries;

namespace Ketras.Map.Rendering;

/// <summary>Builder: turns fences into features. Inclusion and exclusion get different colours because one is a boundary to stay inside, the other a volume to keep out of.</summary>
internal static class FenceLayerBuilder
{
    private const double HandlePixels = 8;

    /// <summary>The operator's fences with handles on the selected one; <c>externalFences</c> (e.g. environment zones) are drawn beneath them, without handles.</summary>
    public static IReadOnlyList<IFeature> Build(
        IReadOnlyList<Fence> fences,
        IReadOnlyList<Fence> externalFences,
        string? selectedId,
        IReadOnlyList<GeoPoint>? draftVertices,
        double resolution)
    {
        var features = new List<IFeature>();

        foreach (var fence in externalFences)
        {
            Add(features, fence, MapStyles.ExternalFenceArea(fence.Inclusion), showHandles: false, resolution);
        }

        foreach (var fence in fences)
        {
            var isSelected = fence.Id == selectedId;
            Add(features, fence, MapStyles.FenceArea(fence.Inclusion, isSelected), showHandles: isSelected, resolution);
        }

        AddDraft(features, draftVertices);
        return features;
    }

    private static void Add(List<IFeature> features, Fence fence, Mapsui.Styles.IStyle area, bool showHandles, double resolution)
    {
        switch (fence.Area)
        {
            case CircleArea circle:
                AddCircle(features, fence, circle, area, showHandles, resolution);
                break;

            case PolygonArea polygon:
                AddPolygon(features, fence, polygon, area, showHandles, resolution);
                break;
        }
    }

    private static void AddCircle(List<IFeature> features, Fence fence, CircleArea circle, Mapsui.Styles.IStyle area, bool showHandles, double resolution)
    {
        var center = MapGeometry.ToMercator(circle.Center);
        var radius = MapGeometry.MetersToMapUnits(Math.Max(circle.RadiusMeters, 1), circle.Center.Latitude);

        features.Add(Geometry(MapGeometry.CreateCirclePolygon(center.X, center.Y, radius), area));

        features.Add(Label(center, fence));

        if (showHandles)
        {
            // One handle on the rim is enough to drag the radius; the centre moves the fence.
            var handleSize = MapGeometry.PixelsToMapUnits(resolution, HandlePixels);
            features.Add(Geometry(MapSymbols.Handle(center, handleSize), MapStyles.FenceHandle()));
            features.Add(Geometry(
                MapSymbols.Handle(new MPoint(center.X + radius, center.Y), handleSize),
                MapStyles.FenceHandle()));
        }
    }

    private static void AddPolygon(List<IFeature> features, Fence fence, PolygonArea polygon, Mapsui.Styles.IStyle area, bool showHandles, double resolution)
    {
        var vertices = polygon.Vertices;
        if (vertices.Length < 3)
        {
            return;
        }

        var ring = new Coordinate[vertices.Length + 1];
        for (var i = 0; i < vertices.Length; i++)
        {
            var point = MapGeometry.ToMercator(vertices[i]);
            ring[i] = new Coordinate(point.X, point.Y);
        }

        ring[^1] = ring[0];

        features.Add(Geometry(new Polygon(new LinearRing(ring)), area));

        features.Add(Label(Centroid(vertices), fence));

        if (showHandles)
        {
            var handleSize = MapGeometry.PixelsToMapUnits(resolution, HandlePixels);

            foreach (var vertex in vertices)
            {
                features.Add(Geometry(
                    MapSymbols.Handle(MapGeometry.ToMercator(vertex), handleSize),
                    MapStyles.FenceHandle()));
            }
        }
    }

    /// <summary>The polygon being drawn right now, before it is committed.</summary>
    private static void AddDraft(List<IFeature> features, IReadOnlyList<GeoPoint>? draftVertices)
    {
        if (draftVertices is not { Count: >= 2 })
        {
            return;
        }

        var coordinates = new Coordinate[draftVertices.Count];
        for (var i = 0; i < draftVertices.Count; i++)
        {
            var point = MapGeometry.ToMercator(draftVertices[i]);
            coordinates[i] = new Coordinate(point.X, point.Y);
        }

        features.Add(Geometry(new LineString(coordinates), MapStyles.FenceDraft()));
    }

    private static IFeature Label(MPoint at, Fence fence)
    {
        var kind = fence.Inclusion ? "IN" : "OUT";
        var text = $"{fence.Id}  {kind}";

        var feature = new PointFeature(at);
        feature.Styles.Add(MapStyles.MapLabel(text));
        return feature;
    }

    private static MPoint Centroid(System.Collections.Immutable.ImmutableArray<GeoPoint> vertices)
    {
        double x = 0, y = 0;

        foreach (var vertex in vertices)
        {
            var point = MapGeometry.ToMercator(vertex);
            x += point.X;
            y += point.Y;
        }

        return new MPoint(x / vertices.Length, y / vertices.Length);
    }

    private static GeometryFeature Geometry(Geometry geometry, Mapsui.Styles.IStyle style)
    {
        var feature = new GeometryFeature { Geometry = geometry };
        feature.Styles.Add(style);
        return feature;
    }
}
