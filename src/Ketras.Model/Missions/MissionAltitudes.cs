using Ketras.Model.Geo;

namespace Ketras.Model.Missions;

/// <summary>
/// Altitudes in the plan's <see cref="AltitudeReference"/> to and from metres above mean sea level. Every reader that
/// compares heights (rules, terrain checks, the encoder, the 3D view) goes through here. <c>groundAt</c> gives the
/// terrain height above mean sea level at a point, null when unknown; only <see cref="AltitudeReference.AboveTerrain"/>
/// needs it.
/// </summary>
public static class MissionAltitudes
{
    /// <summary>
    /// Where an item's <see cref="MissionItem.AltitudeMeters"/> applies: a landing pattern's approach altitude is flown on
    /// its orbit, so the terrain under the orbit centre counts; any other item's at its position.
    /// </summary>
    public static GeoPoint? PositionOf(MissionItem item) =>
        item.Type == MissionItemType.LandingPattern ? MissionItemValues.LandingPatternOrbitCenter(item) : item.Location;

    /// <summary>An altitude in the plan's reference, measured at <paramref name="position"/>, above mean sea level.</summary>
    public static double? AmslOf(MissionSettings settings, double altitude, GeoPoint? position, Func<GeoPoint, double?>? groundAt) =>
        settings.AltitudeReference switch
        {
            AltitudeReference.AboveSeaLevel => altitude,
            AltitudeReference.AboveTerrain => position is { } at && groundAt?.Invoke(at) is { } ground ? ground + altitude : null,
            _ => settings.HomeAltitudeMeters + altitude
        };

    /// <summary>An item's altitude above mean sea level.</summary>
    public static double? AmslOf(MissionSettings settings, MissionItem item, Func<GeoPoint, double?>? groundAt) =>
        AmslOf(settings, item.AltitudeMeters, PositionOf(item), groundAt);

    /// <summary>The touchdown altitude of a landing item above mean sea level; null for other items or unknown ground.</summary>
    public static double? TouchdownAmslOf(MissionSettings settings, MissionItem item, Func<GeoPoint, double?>? groundAt) =>
        MissionItemValues.TouchdownAltitudeMeters(item) is { } touchdown ? AmslOf(settings, touchdown, item.Location, groundAt) : null;

    /// <summary>An item's altitude above the planned home.</summary>
    public static double? RelativeToHomeOf(MissionSettings settings, MissionItem item, Func<GeoPoint, double?>? groundAt) =>
        AmslOf(settings, item, groundAt) - settings.HomeAltitudeMeters;

    /// <summary>A height above mean sea level as an altitude in <paramref name="reference"/> at <paramref name="position"/>.</summary>
    public static double? FromAmsl(MissionSettings settings, AltitudeReference reference, double amsl, GeoPoint? position, Func<GeoPoint, double?>? groundAt) =>
        reference switch
        {
            AltitudeReference.AboveSeaLevel => amsl,
            AltitudeReference.AboveTerrain => position is { } at && groundAt?.Invoke(at) is { } ground ? amsl - ground : null,
            _ => amsl - settings.HomeAltitudeMeters
        };

    /// <summary>
    /// The mission in another reference, flying the same heights above mean sea level. Null when an item's terrain is
    /// unknown; <paramref name="failedItem"/> is then its 1-based number.
    /// </summary>
    public static Mission? Convert(Mission mission, AltitudeReference to, Func<GeoPoint, double?>? groundAt, out int? failedItem)
    {
        failedItem = null;
        var settings = mission.Settings;
        if (settings.AltitudeReference == to)
        {
            return mission;
        }

        var items = mission.Items.ToBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.UsesLocation)
            {
                continue;
            }

            if (AmslOf(settings, item, groundAt) is not { } amsl || FromAmsl(settings, to, amsl, PositionOf(item), groundAt) is not { } altitude)
            {
                failedItem = i + 1;
                return null;
            }

            item = item with { AltitudeMeters = Math.Round(altitude, 1) };
            if (item.Type == MissionItemType.LandingPattern)
            {
                if (TouchdownAmslOf(settings, items[i], groundAt) is not { } touchdownAmsl
                    || FromAmsl(settings, to, touchdownAmsl, item.Location, groundAt) is not { } touchdown)
                {
                    failedItem = i + 1;
                    return null;
                }

                item = item with { Param1 = Math.Round(touchdown, 1) };
            }

            items[i] = item;
        }

        return mission with { Settings = settings with { AltitudeReference = to }, Items = items.ToImmutable() };
    }

    /// <summary>A short suffix naming the reference, for altitudes shown to the operator: "m", "m AMSL", "m AGL".</summary>
    public static string UnitOf(AltitudeReference reference) => reference switch
    {
        AltitudeReference.AboveSeaLevel => "m AMSL",
        AltitudeReference.AboveTerrain => "m AGL",
        _ => "m"
    };
}
