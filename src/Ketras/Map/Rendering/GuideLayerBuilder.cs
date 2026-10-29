using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;

namespace Ketras.Map.Rendering;

/// <summary>Builder: turns the host's guide drawings into dashed rings, route marks and handle squares with their labels.</summary>
internal static class GuideLayerBuilder
{
    public static IReadOnlyList<IFeature> Build(IReadOnlyList<MapGuide> guides)
    {
        var features = new List<IFeature>();
        foreach (var guide in guides)
        {
            var center = MapGeometry.ToMercator(guide.Position);
            if (guide.Shape == MapGuideShape.Ring)
            {
                var radius = MapGeometry.MetersToMapUnits(Math.Max(guide.RadiusMeters, 1), guide.Position.Latitude);
                var ring = new GeometryFeature(LoiterGeometry.Ring(center, radius));
                ring.Styles.Add(MapStyles.GuideRing(guide.Warning));
                features.Add(ring);

                // The label sits on the ring's north point, so rings around one place do not stack their labels.
                var top = new PointFeature(new MPoint(center.X, center.Y + radius));
                top.Styles.Add(MapStyles.GuideLabel(guide.Label, guide.Warning, new Offset(0, -10)));
                features.Add(top);
                continue;
            }

            var point = new PointFeature(center);
            point.Styles.Add(MapStyles.GuidePoint(guide.Shape == MapGuideShape.Handle, guide.Warning));
            point.Styles.Add(MapStyles.GuideLabel(guide.Label, guide.Warning, new Offset(0, 16)));
            features.Add(point);
        }

        return features;
    }
}
