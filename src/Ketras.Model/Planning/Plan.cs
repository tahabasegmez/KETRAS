using System.Collections.Immutable;
using Ketras.Model.Fences;
using Ketras.Model.Missions;
using Ketras.Model.Terrain;
using Ketras.Model.Vehicles;

namespace Ketras.Model.Planning;

/// <summary>What the operator plans for one vehicle: the mission and the geofence.</summary>
public sealed record Plan
{
    /// <summary>An empty plan.</summary>
    public static Plan Empty { get; } = new();

    /// <summary>The mission.</summary>
    public Mission Mission { get; init; } = Mission.Empty;

    /// <summary>The operator's geofence zones.</summary>
    public ImmutableList<Fence> Fences { get; init; } = [];
}

/// <summary>Runs every plan check: mission rules, fence rules and, with a grid, the terrain rules.</summary>
public static class PlanRules
{
    /// <summary>
    /// Findings for <paramref name="plan"/>. <paramref name="extraFences"/> are zones that will be uploaded with the
    /// operator's fences (e.g. imposed environment zones), so the fence checks see the whole geofence.
    /// </summary>
    public static IReadOnlyList<PlanFinding> Validate(Plan plan, VehicleLimits? limits = null, TerrainGrid? terrain = null, IEnumerable<Fence>? extraFences = null) =>
    [
        .. MissionRules.Validate(plan.Mission, limits, terrain is null ? null : terrain.HeightAt),
        .. FenceRules.Validate([.. plan.Fences, .. extraFences ?? []], plan.Mission.Settings.PlannedHome),
        .. TerrainRules.Validate(plan.Mission, terrain, limits)
    ];
}
