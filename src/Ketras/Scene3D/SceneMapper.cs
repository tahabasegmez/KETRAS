using Ketras.Controls.Scene3D;
using Ketras.Model.Environment;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Model.Vehicles;
using Ketras.Station.Imagery;

namespace Ketras.Scene3D;

/// <summary>
/// Mapper (view side): Station data to the 3D scene's items. The datum is the plan's home altitude; relative
/// altitudes become AMSL on it. Fences carry no altitude (a vehicle geofence has none), so they rise from home to a
/// little above the highest planned altitude; zones use their own floor and ceiling where they have one.
/// </summary>
public static class SceneMapper
{
    /// <summary>A fence's height when the plan has no higher altitude, m above home.</summary>
    public const double MinimumFenceHeight = 150;

    /// <summary>The scene's origin and datum from the plan.</summary>
    public static SceneOptions Options(Plan plan)
    {
        var settings = plan.Mission.Settings;
        return new SceneOptions
        {
            Origin = settings.PlannedHome.IsUnset ? null : settings.PlannedHome,
            HomeAltitudeMeters = settings.HomeAltitudeMeters
        };
    }

    /// <summary>
    /// The route: every item with a position, numbered as the plan shows it, at its height above sea level in the plan's
    /// altitude reference (<paramref name="groundAt"/> for above-terrain plans; an unknown ground falls back to the home).
    /// </summary>
    public static IReadOnlyList<ScenePathPoint> Path(Plan plan, Func<GeoPoint, double?>? groundAt = null)
    {
        var settings = plan.Mission.Settings;
        return [.. plan.Mission.Items
            .Select((item, index) => (item, index))
            .Where(entry => entry.item.Location is not null)
            .Select(entry => new ScenePathPoint(entry.index + 1, entry.item.Location!.Value,
                MissionAltitudes.AmslOf(settings, entry.item.AltitudeMeters, entry.item.Location, groundAt) ?? settings.HomeAltitudeMeters + entry.item.AltitudeMeters,
                Planning.Missions.MissionLabels.Of(entry.item.Type), entry.item.Phase))];
    }

    /// <summary>The plan's fences and the environment's keep-out and stay-in zones as volumes.</summary>
    public static IReadOnlyList<SceneVolume> Volumes(Plan plan, IReadOnlyList<Zone> zones)
    {
        var home = plan.Mission.Settings.HomeAltitudeMeters;
        var highest = plan.Mission.Items.Select(i => i.AltitudeMeters).DefaultIfEmpty(0).Max();
        var ceiling = home + Math.Max(MinimumFenceHeight, highest + 30);
        var fences = plan.Fences.Select(f => new SceneVolume(f.Id, f.Id, f.Area, f.Inclusion, home, ceiling));
        var areas = zones.Where(z => z.Kind != ZoneKind.Advisory).Select(z => new SceneVolume(
            $"{z.Source}:{z.Id}", z.Name ?? z.Id, z.Area, z.Kind == ZoneKind.StayIn, z.FloorMeters ?? home, z.CeilingMeters ?? ceiling));
        return [.. fences, .. areas];
    }

    /// <summary>The active vehicle as an aircraft; null without a position.</summary>
    public static SceneAircraft? Ownship(VehicleSnapshot? snapshot) =>
        snapshot is { Position: { } position, AltitudeAmslMeters: { } altitude }
            ? new SceneAircraft(Scene3DViewModel.SceneId(snapshot.Id), snapshot.Name, position, altitude, snapshot.HeadingDegrees ?? 0,
                snapshot.PitchDegrees ?? 0, snapshot.RollDegrees ?? 0, snapshot.GroundSpeedMps ?? 0, snapshot.ClimbRateMps ?? 0, snapshot.Mode)
            : null;

    /// <summary>Traffic as aircraft; one without an altitude sits at the datum.</summary>
    public static IReadOnlyList<SceneAircraft> Traffic(IReadOnlyList<TrafficTarget> traffic, double homeAltitude) =>
        [.. traffic.Select(t => new SceneAircraft(t.Id, t.Callsign ?? t.Id, t.Position, t.AltitudeMeters ?? homeAltitude, t.HeadingDegrees ?? 0,
            GroundSpeedMps: t.GroundSpeedMps ?? 0, VerticalSpeedMps: t.VerticalSpeedMps ?? 0, Mode: t.Category.ToString()))];

    /// <summary>The scene's ground style for a surface.</summary>
    public static SceneGround Ground(SceneSurface surface) => surface == SceneSurface.Grey ? SceneGround.Relief : SceneGround.Imagery;

    /// <summary>Map tiles to drape over the terrain; null without any.</summary>
    public static SceneImagery? Imagery(ImageryMosaic? mosaic) =>
        mosaic is null ? null : new SceneImagery(mosaic.Range, [.. mosaic.Tiles.Select(t => new SceneImageryTile(t.X, t.Y, t.Encoded))], mosaic.MaxZoom);

    /// <summary>Points of interest.</summary>
    public static IReadOnlyList<ScenePoint> Points(IReadOnlyList<PointOfInterest> points) =>
        [.. points.Select(p => new ScenePoint(p.Name ?? p.Id, p.Location))];
}
