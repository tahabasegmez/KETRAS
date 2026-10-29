using System.Globalization;
using Ketras.Model.Geo;
using Ketras.Model.Missions;

namespace Ketras.Terrain;

/// <summary>
/// Puts a plan's altitudes on the terrain (docs/MISSION.md §10.1): the home altitude becomes the ground height at the
/// planned home, and every touchdown (Land, LandingPattern) the ground there, in the plan's altitude reference:
/// relative to the home, or above sea level. Above terrain a touchdown is 0 by definition and stays as planned. PX4
/// flies relative altitudes above the ground it armed on and, without a range sensor, takes the land point's altitude
/// as the ground for the flare (FixedWingModeManager.cpp:2677-2700), so the ground, not the GPS altitude, is the reference.
/// </summary>
public static class TerrainAlignment
{
    /// <summary>The aligned mission and what changed, in operator-facing English; the same mission when nothing did.</summary>
    public static (Mission Mission, IReadOnlyList<string> Changes) Align(Mission mission, Func<GeoPoint, double?> groundAt)
    {
        var changes = new List<string>();
        var home = mission.Settings.PlannedHome;
        if (home.IsUnset || groundAt(home) is not { } homeGround)
        {
            return (mission, changes);
        }

        var homeAltitude = Math.Round(homeGround, 1);
        if (Math.Abs(mission.Settings.HomeAltitudeMeters - homeAltitude) > 0.05)
        {
            changes.Add(Format("home altitude {0:F1} → {1:F1} m", mission.Settings.HomeAltitudeMeters, homeAltitude));
            mission = MissionEditor.UpdateSettings(mission, settings => settings with { HomeAltitudeMeters = homeAltitude });
        }

        foreach (var (item, index) in mission.Items.Select((item, index) => (item, index)).ToList())
        {
            if (item.Location is not { } touchdown || MissionItemValues.TouchdownAltitudeMeters(item) is not { } current
                || groundAt(touchdown) is not { } ground)
            {
                continue;
            }

            if (mission.Settings.AltitudeReference == AltitudeReference.AboveTerrain)
            {
                continue;
            }

            var relative = Math.Round(mission.Settings.AltitudeReference == AltitudeReference.AboveSeaLevel ? ground : ground - homeAltitude, 1);
            if (Math.Abs(current - relative) <= 0.05)
            {
                continue;
            }

            changes.Add(Format("item {0} touchdown {1:F1} → {2:F1} m", index + 1, current, relative));
            mission = MissionEditor.Update(mission, item.Id, i => i.Type == MissionItemType.Land ? i with { AltitudeMeters = relative } : i with { Param1 = relative }).Mission;
        }

        return (mission, changes);
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
}
