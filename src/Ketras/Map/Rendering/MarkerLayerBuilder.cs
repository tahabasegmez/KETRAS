using Ketras.Model.Environment;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Styles;

namespace Ketras.Map.Rendering;

/// <summary>Builder: turns points of interest into labelled diamonds; symbols are pixel-sized, so no resolution is needed.</summary>
internal static class MarkerLayerBuilder
{
    private const double LabelOffsetPixels = 16;

    public static IReadOnlyList<IFeature> Build(IReadOnlyList<PointOfInterest> markers)
    {
        var features = new List<IFeature>(markers.Count);

        foreach (var marker in markers)
        {
            var feature = new PointFeature(MapGeometry.ToMercator(marker.Location));
            feature.Styles.Add(MapStyles.MapMarker(MapPalette.PointOfInterest));

            if ((marker.Name ?? marker.Id) is { Length: > 0 } label)
            {
                feature.Styles.Add(MapStyles.MapLabel(label, offset: new Offset(0, LabelOffsetPixels)));
            }

            features.Add(feature);
        }

        return features;
    }
}
