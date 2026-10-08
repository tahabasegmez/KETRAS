using Ketras.Model.Geo;
using Ketras.Model.Planning;

namespace Ketras.Model.Fences;

/// <summary>
/// Checks a fence set the way an autopilot geofence treats it: every zone applies at once, and a zone that excludes
/// the home is discarded (PX4 <c>Geofence::checkPointAgainstAllGeofences</c>, see docs/MISSION.md).
/// </summary>
public static class FenceRules
{
    /// <summary>Problems with the fences as a whole. An unset planned home skips the home checks.</summary>
    public static IReadOnlyList<PlanFinding> Validate(IReadOnlyList<Fence> fences, GeoPoint plannedHome)
    {
        var findings = new List<PlanFinding>();

        var inclusions = fences.Where(fence => fence.Inclusion).Select(fence => fence.Id).ToList();
        if (inclusions.Count > 1)
        {
            findings.Add(new PlanFinding(FindingSource.Fences, "fences.several-inclusions",
                $"{string.Join(", ", inclusions)}: the vehicle must stay inside all of these at once, not just one."));
        }

        if (plannedHome.IsUnset)
        {
            return findings;
        }

        foreach (var fence in fences.Where(fence => fence.Area.Contains(plannedHome) != fence.Inclusion))
        {
            findings.Add(fence.Inclusion
                ? new PlanFinding(FindingSource.Fences, "fences.home-outside", $"{fence.Id}: home is outside this zone, so the autopilot discards it.", FenceId: fence.Id)
                : new PlanFinding(FindingSource.Fences, "fences.home-inside", $"{fence.Id}: home is inside this keep-out zone, so the autopilot discards it.", FenceId: fence.Id));
        }

        return findings;
    }
}
