using System.Globalization;
using Ketras.Model.Geo;
using Ketras.Model.Planning;
using Ketras.Model.Vehicles;

namespace Ketras.Model.Missions;

/// <summary>
/// Mission ordering (Takeoff → Cruise → Landing, one takeoff, nothing after the landing)
/// and checks of a plan against the vehicle's limits, including PX4's fixed-wing landing feasibility checks.
/// </summary>
public static class MissionRules
{
    private const double GravityMps2 = 9.80665;

    /// <summary>Whether a new item of <paramref name="type"/> may be added; <paramref name="reason"/> says why not.</summary>
    public static bool CanAdd(Mission mission, MissionItemType type, out string? reason)
    {
        reason = MissionItemCatalog.PhaseOf(type) switch
        {
            MissionPhase.Takeoff when HasPhase(mission, MissionPhase.Takeoff) => "The plan already has a takeoff.",
            MissionPhase.Landing when HasPhase(mission, MissionPhase.Landing) => "The plan already ends with a landing item.",
            _ => null
        };
        return reason is null;
    }

    /// <summary>Where a new item goes: the end of its own phase, keeping the list in phase order.</summary>
    public static int InsertIndexFor(Mission mission, MissionItemType type)
    {
        var phase = MissionItemCatalog.PhaseOf(type);
        var index = mission.Items.FindIndex(item => item.Phase > phase);
        return index < 0 ? mission.Items.Count : index;
    }

    /// <summary>
    /// Points every jump's <c>Param1</c> at the current index of its <see cref="MissionItem.JumpTargetId"/>, or -1 when
    /// the target is gone. A jump without a target id adopts the item at <c>Param1</c>, as a plan loaded from outside
    /// carries only the index.
    /// </summary>
    public static Mission SyncJumpTargets(Mission mission)
    {
        var items = mission.Items;
        var builder = items.ToBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var jump = items[i];
            if (jump.Type != MissionItemType.DoJump)
            {
                continue;
            }

            var targetId = jump.JumpTargetId ?? (jump.Param1 >= 0 && jump.Param1 < items.Count ? items[(int)jump.Param1].Id : null);
            var index = targetId is { } id ? items.FindIndex(item => item.Id == id) : -1;
            if (index != (int)jump.Param1 || targetId != jump.JumpTargetId)
            {
                builder[i] = jump with { JumpTargetId = targetId, Param1 = index };
            }
        }

        return mission with { Items = builder.ToImmutable() };
    }

    /// <summary>
    /// Phase order, duplicates, jump targets, the vehicle's speed, turn and glide limits, and a landing that would abort
    /// on a rangefinder dropout. Empty when well formed. <paramref name="groundAt"/> gives the terrain for plans whose
    /// altitudes are above terrain.
    /// </summary>
    public static IReadOnlyList<PlanFinding> Validate(Mission mission, VehicleLimits? limits = null, Func<GeoPoint, double?>? groundAt = null)
    {
        limits ??= VehicleLimits.Default;
        var findings = new List<PlanFinding>();
        var items = mission.Items;

        PlanFinding Finding(string code, string message, int? item = null, GeoPoint? location = null) =>
            new(FindingSource.Mission, code, message, item, location);

        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].Phase < items[i - 1].Phase)
            {
                findings.Add(Finding("mission.phase-order", Format("Item {0} ({1}) comes after a later flight phase.", i + 1, items[i].Type), i + 1));
                break;
            }
        }

        if (items.Count(item => item.Phase == MissionPhase.Takeoff) > 1)
        {
            findings.Add(Finding("mission.takeoffs", "The plan has more than one takeoff."));
        }

        if (items.Count(item => item.Phase == MissionPhase.Landing) > 1)
        {
            findings.Add(Finding("mission.landings", "The plan has more than one landing item."));
        }

        // PX4 fixed-wing landing (FixedWingModeManager::getLandingTerrainAltitudeEstimate): with an abort bit of
        // FW_LND_ABORT set, a rangefinder that misses or loses the ground abandons the landing and the vehicle loiters.
        var landing = mission.Settings.Landing;
        if (items.FirstOrDefault(item => item.Type is MissionItemType.LandingPattern or MissionItemType.Land) is { } land
            && landing.TerrainUse != LandingTerrainUse.Off && (landing.AbortIfTerrainNotFound || landing.AbortIfTerrainLost))
        {
            var when = landing.AbortIfTerrainLost ? "loses the ground for a second" : "never finds the ground";
            findings.Add(Finding("mission.landing-abort", Format("Item {0}: if the rangefinder {1}, the vehicle abandons the landing and loiters (landing settings, FW_LND_ABORT).",
                items.IndexOf(land) + 1, when), items.IndexOf(land) + 1, land.Location));
        }

        var speed = mission.Settings.CruiseSpeedMps;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var number = i + 1;

            switch (item.Type)
            {
                case MissionItemType.DoJump when item.Param1 < 0 || item.Param1 >= items.Count:
                    findings.Add(Finding("mission.jump-target", Format("Item {0}: the jump has no target item.", number), number));
                    break;

                case MissionItemType.ChangeSpeed:
                    speed = item.Param2;
                    if (speed < limits.MinAirspeedMps || speed > limits.MaxAirspeedMps)
                    {
                        findings.Add(Finding("mission.airspeed", Format("Item {0}: {1:F0} m/s is outside {2:F0}-{3:F0} m/s; the vehicle clamps it.",
                            number, speed, limits.MinAirspeedMps, limits.MaxAirspeedMps), number));
                    }

                    break;

                // Heights are compared above mean sea level, as PX4 does; unknown terrain (above-terrain plans) skips the check.
                case MissionItemType.LandingPattern when LandingDrop(mission.Settings, item, groundAt) is <= 0:
                    findings.Add(Finding("mission.landing-height", Format("Item {0}: the approach altitude ({1:F0} m) is not above the touchdown ({2:F0} m); PX4 rejects the mission.",
                        number, item.AltitudeMeters, item.Param1), number, item.Location));
                    break;

                case MissionItemType.LandingPattern when item.Param3 > 0 && LandingDrop(mission.Settings, item, groundAt) is { } fall
                                                         && Math.Atan2(fall, item.Param3) * 180 / Math.PI is var slope && slope > limits.MaxLandingSlopeDegrees:
                    findings.Add(Finding("mission.landing-slope", Format("Item {0}: the {1:F1}° glide slope is steeper than {2:F1}°, so PX4 rejects the mission; lengthen the approach to {3:F0} m or lower it.",
                        number, slope, limits.MaxLandingSlopeDegrees,
                        MissionItemFactory.ApproachLengthFor(fall, limits.MaxLandingSlopeDegrees)), number, item.Location));
                    break;

                // PX4 checks a plain land against the item right before it (FeasibilityChecker::checkFixedWingLandApproach).
                case MissionItemType.Land when i > 0 && items[i - 1] is { Location: { } from } previous
                                               && previous.Type is MissionItemType.Waypoint or MissionItemType.LoiterToAltitude
                                               && item.Location is { } touchdown
                                               && MissionAltitudes.AmslOf(mission.Settings, previous, groundAt) is { } previousAmsl
                                               && MissionAltitudes.AmslOf(mission.Settings, item, groundAt) is { } touchdownAmsl:
                    var drop = previousAmsl - touchdownAmsl;
                    var distance = GeoMath.DistanceMeters(from, touchdown);
                    if (previous.Type == MissionItemType.LoiterToAltitude && MissionItemValues.LoiterRadiusMeters(previous) is { } orbit)
                    {
                        distance = Math.Sqrt(Math.Max((distance * distance) - (orbit * orbit), 0));
                    }

                    if (drop <= 0)
                    {
                        findings.Add(Finding("mission.land-height", Format("Item {0}: the approach point (item {1}) is not above the touchdown; PX4 rejects the mission.", number, i), number, touchdown));
                    }
                    else if (distance > 0 && Math.Atan2(drop, distance) * 180 / Math.PI is var landSlope && landSlope > limits.MaxLandingSlopeDegrees)
                    {
                        findings.Add(Finding("mission.land-slope", Format("Item {0}: the {1:F1}° glide slope from item {2} is steeper than {3:F1}°, so PX4 rejects the mission; move item {2} to {4:F0} m away or lower it.",
                            number, landSlope, i, limits.MaxLandingSlopeDegrees, MissionItemFactory.ApproachLengthFor(drop, limits.MaxLandingSlopeDegrees)), number, touchdown));
                    }

                    break;
            }

            if (MissionItemValues.LoiterRadiusMeters(item) is { } radius)
            {
                var turnSpeed = Math.Clamp(speed, limits.MinAirspeedMps, limits.MaxAirspeedMps);
                var minimum = MinimumTurnRadiusMeters(turnSpeed, limits.MaxBankDegrees);
                if (radius < minimum)
                {
                    findings.Add(Finding("mission.turn-radius", Format("Item {0}: a {1:F0} m orbit is tighter than the {2:F0} m the aircraft can turn at {3:F0} m/s.",
                        number, radius, minimum, turnSpeed), number, item.Location));
                }
            }
        }

        return findings;
    }

    /// <summary>Radius of a level turn at the given airspeed and bank angle.</summary>
    public static double MinimumTurnRadiusMeters(double airspeedMps, double bankDegrees) =>
        airspeedMps * airspeedMps / (GravityMps2 * Math.Tan(bankDegrees * Math.PI / 180d));

    /// <summary>How far a landing pattern descends from its orbit to the touchdown, above mean sea level; null when unknown.</summary>
    internal static double? LandingDrop(MissionSettings settings, MissionItem item, Func<GeoPoint, double?>? groundAt) =>
        MissionAltitudes.AmslOf(settings, item, groundAt) - MissionAltitudes.TouchdownAmslOf(settings, item, groundAt);

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

    private static bool HasPhase(Mission mission, MissionPhase phase) => mission.Items.Any(item => item.Phase == phase);
}
