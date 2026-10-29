using Ketras.Geo;

namespace Ketras.Planning;

/// <summary>Which check produced a finding.</summary>
public enum FindingSource
{
    /// <summary>Mission ordering and the vehicle's limits.</summary>
    Mission,

    /// <summary>The geofence set.</summary>
    Fences,

    /// <summary>The plan against the terrain.</summary>
    Terrain,

    /// <summary>The flight against the battery and the wind.</summary>
    Flight
}

/// <summary>
/// One problem a check found in a plan. <see cref="Code"/> is stable (for keys and translations); <see cref="Message"/>
/// is the English text.
/// </summary>
/// <param name="Source">The check.</param>
/// <param name="Code">Stable identifier of the rule, e.g. "terrain.leg-contact".</param>
/// <param name="Message">Operator-facing English text.</param>
/// <param name="ItemNumber">1-based mission item it belongs to, if any.</param>
/// <param name="Location">Where on the map it applies, if anywhere.</param>
/// <param name="FenceId">Fence it belongs to, if any.</param>
public sealed record PlanFinding(
    FindingSource Source,
    string Code,
    string Message,
    int? ItemNumber = null,
    GeoPoint? Location = null,
    string? FenceId = null);
