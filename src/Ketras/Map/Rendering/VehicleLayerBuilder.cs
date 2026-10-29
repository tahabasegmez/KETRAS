using Ketras.Map.Telemetry;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using NetTopologySuite.Geometries;

using MColor = Mapsui.Styles.Color;

namespace Ketras.Map.Rendering;

/// <summary>Builder: turns live tracks into the four vehicle layers. Ownship and traffic stay apart so they can be toggled independently; trails stay off the marker layer so history never covers it.</summary>
internal static class VehicleLayerBuilder
{
    /// <summary>Smallest on-screen marker span, in pixels.</summary>
    private const double MinMarkerPixels = 14;

    /// <summary>Whole-feature opacity for a stale track; set per style because glows are already faded.</summary>
    private const float StaleOpacity = 0.35f;

    internal readonly record struct VehicleFeatures(
        IReadOnlyList<IFeature> Ownship,
        IReadOnlyList<IFeature> Traffic,
        IReadOnlyList<IFeature> OwnshipTrail,
        IReadOnlyList<IFeature> TrafficTrails);

    public static VehicleFeatures Build(
        VehicleTrackStore store,
        VehicleLayerOptions ownshipOptions,
        VehicleLayerOptions trafficOptions,
        double resolution)
    {
        var ownship = new List<IFeature>();
        var traffic = new List<IFeature>();
        var ownshipTrail = new List<IFeature>();
        var trafficTrails = new List<IFeature>();

        foreach (var track in store.Tracks)
        {
            if (!track.HasFirstFix)
            {
                continue;
            }

            var options = track.IsOwnship ? ownshipOptions : trafficOptions;
            var color = track.IsOwnship
                ? MapPalette.Parse(options.MarkerColorHex, MapPalette.VehicleFallback)
                : store.TrafficColor(track);

            var marker = CreateMarker(track, options, color, resolution);

            if (options.ShowLabel)
            {
                // The label sits to the right of the marker and clears half of it plus a gap.
                var offsetX = (Math.Max(options.MarkerSize, MinMarkerPixels) / 2) + 6;
                marker.Styles.Add(MapStyles.VehicleLabel(BuildLabel(track, options), offsetX));
            }

            if (track.IsStale)
            {
                Dim(marker);
            }

            if (track.IsOwnship)
            {
                ownship.Add(marker);

                if (options.ShowTrail)
                {
                    // A white ownship would leave a white trail; the trail keeps its own colour.
                    AddTrail(ownshipTrail, track, options, MapPalette.Parse(options.TrailColorHex, MapPalette.VehicleFallback));
                }
            }
            else
            {
                traffic.Add(marker);

                if (options.ShowTrail)
                {
                    AddTrail(trafficTrails, track, options, color);
                }
            }
        }

        return new VehicleFeatures(ownship, traffic, ownshipTrail, trafficTrails);
    }

    /// <summary>
    /// Arrow and plane markers are real geometry, so heading reads at a glance and the size stays
    /// constant on screen. The other kinds keep Mapsui's point styles.
    /// </summary>
    private static IFeature CreateMarker(
        VehicleTrack track,
        VehicleLayerOptions options,
        MColor color,
        double resolution)
    {
        if (options.MarkerKind == VehicleMarkerKind.Arrow)
        {
            var length = MapGeometry.PixelsToMapUnits(resolution, Math.Max(options.MarkerSize, MinMarkerPixels));
            var arrow = new GeometryFeature { Geometry = MapSymbols.Arrow(track.CurrentWorld, track.CurrentHeading, length) };
            arrow.Styles.Add(MapStyles.VehicleArrow(color));
            return arrow;
        }

        if (options.MarkerKind != VehicleMarkerKind.Plane)
        {
            var point = new PointFeature(track.CurrentWorld);
            point.Styles.Add(MapStyles.VehicleMarker(options, track.CurrentHeading, color));
            return point;
        }

        var span = MapGeometry.PixelsToMapUnits(resolution, Math.Max(options.MarkerSize, MinMarkerPixels));

        var feature = new GeometryFeature
        {
            Geometry = MapSymbols.Aircraft(track.CurrentWorld, track.CurrentHeading, span)
        };

        feature.Styles.Add(MapStyles.Glow(color, 6f));
        feature.Styles.Add(MapStyles.Silhouette(color));
        return feature;
    }

    /// <summary>"NAME  120 m  18 m/s": the callsign, then altitude and ground speed.</summary>
    private static string BuildLabel(VehicleTrack track, VehicleLayerOptions options)
    {
        var name = !string.IsNullOrWhiteSpace(options.LabelText) ? options.LabelText : track.DisplayName;
        var label = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{name.ToUpperInvariant()}  {track.AltitudeMeters:F0} m  {track.GroundSpeedMetersPerSecond:F0} m/s");

        return track.IsStale ? $"{label}  STALE" : label;
    }

    private static void Dim(IFeature feature)
    {
        foreach (var style in feature.Styles)
        {
            style.Opacity = StaleOpacity;
        }
    }

    private static void AddTrail(
        List<IFeature> target,
        VehicleTrack track,
        VehicleLayerOptions options,
        MColor color)
    {
        var points = track.Trail;
        if (points.Count < 2)
        {
            return;
        }

        var coordinates = new Coordinate[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            coordinates[i] = new Coordinate(points[i].X, points[i].Y);
        }

        var line = new GeometryFeature { Geometry = new LineString(coordinates) };
        line.Styles.Add(MapStyles.Glow(color, options.TrailWidth * 2.6f, 0.24));
        line.Styles.Add(MapStyles.Stroke(color, options.TrailWidth));

        if (track.IsStale)
        {
            Dim(line);
        }

        target.Add(line);
    }
}
