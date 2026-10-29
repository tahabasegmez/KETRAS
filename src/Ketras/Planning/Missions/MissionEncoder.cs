using Ketras.Geo;
using Ketras.Mavlink.Messages;
using Ketras.Planning.Fences;

using WireItem = Ketras.Mavlink.Protocols.MissionItem;

namespace Ketras.Planning.Missions;

/// <summary>A mission ready for upload: the wire items and, per wire sequence number, the plan item it came from (-1 for added items).</summary>
/// <param name="Items">MISSION_ITEM_INT items in sequence order.</param>
/// <param name="PlanIndexOfSequence">Plan item index for each wire sequence number.</param>
/// <param name="Blockers">Why the mission must not be uploaded; empty when it may.</param>
public sealed record EncodedMission(IReadOnlyList<WireItem> Items, IReadOnlyList<int> PlanIndexOfSequence, IReadOnlyList<string> Blockers);

/// <summary>
/// Encoder: the plan model to MAVLink mission and geofence items (MISSION_ITEM_INT), following docs/MISSION.md:
/// LandingPattern expands to DO_LAND_START + LOITER_TO_ALT + NAV_LAND, jump targets become wire sequence numbers,
/// sequence 0 is a home item only when the autopilot wants one (PX4 does not), fences go polygons first. NaN parameters
/// (an unspecified yaw) travel as NaN. Positions go in the plan's altitude reference: relative to home as
/// GLOBAL_RELATIVE_ALT, above sea level as GLOBAL, above terrain as GLOBAL with the terrain height added here.
/// </summary>
public static class MissionEncoder
{
    private readonly record struct Definition(MavCmd Command, MavFrame Frame);

    private static readonly Dictionary<MissionItemType, Definition> Table = new()
    {
        [MissionItemType.Takeoff] = new(MavCmd.NavTakeoff, MavFrame.GlobalRelativeAlt),
        [MissionItemType.Waypoint] = new(MavCmd.NavWaypoint, MavFrame.GlobalRelativeAlt),
        [MissionItemType.LoiterUnlimited] = new(MavCmd.NavLoiterUnlim, MavFrame.GlobalRelativeAlt),
        [MissionItemType.LoiterTime] = new(MavCmd.NavLoiterTime, MavFrame.GlobalRelativeAlt),
        [MissionItemType.LoiterToAltitude] = new(MavCmd.NavLoiterToAlt, MavFrame.GlobalRelativeAlt),
        [MissionItemType.RegionOfInterest] = new(MavCmd.DoSetRoiLocation, MavFrame.GlobalRelativeAlt),
        [MissionItemType.ChangeSpeed] = new(MavCmd.DoChangeSpeed, MavFrame.Mission),
        [MissionItemType.TakePhoto] = new(MavCmd.ImageStartCapture, MavFrame.Mission),
        [MissionItemType.StartVideo] = new(MavCmd.VideoStartCapture, MavFrame.Mission),
        [MissionItemType.StopVideo] = new(MavCmd.VideoStopCapture, MavFrame.Mission),
        [MissionItemType.CameraTriggerDistance] = new(MavCmd.DoSetCamTriggDist, MavFrame.Mission),
        [MissionItemType.DelaySeconds] = new(MavCmd.ConditionDelay, MavFrame.Mission),
        [MissionItemType.DoJump] = new(MavCmd.DoJump, MavFrame.Mission),
        [MissionItemType.Land] = new(MavCmd.NavLand, MavFrame.GlobalRelativeAlt),
        [MissionItemType.ReturnToLaunch] = new(MavCmd.NavReturnToLaunch, MavFrame.Mission)
    };

    /// <summary>MAV_CMD and MAV_FRAME of a simple item type; LandingPattern has none (it expands).</summary>
    public static (MavCmd Command, MavFrame Frame) WireOf(MissionItemType type) =>
        Table.TryGetValue(type, out var definition) ? (definition.Command, definition.Frame)
        : throw new ArgumentException($"{type} expands into several items", nameof(type));

    /// <summary>The item type a MAV_CMD reads back as; LandingPattern is never resolved from a single command.</summary>
    public static bool TryResolve(MavCmd command, out MissionItemType type)
    {
        foreach (var (candidate, definition) in Table)
        {
            if (definition.Command == command)
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>
    /// Mission items for upload; <paramref name="homeAtSequenceZero"/> comes from the autopilot profile and
    /// <paramref name="groundAt"/> (terrain m AMSL at a point) is needed for a plan whose altitudes are above terrain.
    /// </summary>
    public static EncodedMission Encode(Mission mission, bool homeAtSequenceZero, Func<GeoPoint, double?>? groundAt = null)
    {
        var settings = mission.Settings;
        var relative = settings.AltitudeReference == AltitudeReference.RelativeToHome;
        var positionFrame = relative ? MavFrame.GlobalRelativeAlt : MavFrame.Global;
        float? Altitude(double value, GeoPoint? at) =>
            relative ? (float)value : MissionAltitudes.AmslOf(settings, value, at, groundAt) is { } amsl ? (float)amsl : null;

        var items = mission.Items;
        var blockers = new List<string>();
        var offset = homeAtSequenceZero ? 1 : 0;

        var firstSequence = new int[items.Count];
        var sequence = offset;
        for (var i = 0; i < items.Count; i++)
        {
            firstSequence[i] = sequence;
            sequence += items[i].Type == MissionItemType.LandingPattern ? 3 : 1;
        }

        var wire = new List<WireItem>();
        var planIndex = new List<int>();

        void Add(WireItem item, int index)
        {
            wire.Add(item);
            planIndex.Add(index);
        }

        if (homeAtSequenceZero)
        {
            var home = mission.Settings.PlannedHome;
            Add(WireItem.At(MavFrame.Global, MavCmd.NavWaypoint, home.Latitude, home.Longitude, (float)mission.Settings.HomeAltitudeMeters), -1);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            switch (item.Type)
            {
                case MissionItemType.LandingPattern when item.Location is { } touchdown:
                    var center = MissionItemValues.LandingPatternOrbitCenter(item)!.Value;
                    if (Altitude(item.AltitudeMeters, center) is not { } approachAltitude || Altitude(item.Param1, touchdown) is not { } touchdownAltitude)
                    {
                        blockers.Add($"item {i + 1}: no terrain data under the landing pattern for its above-terrain altitudes");
                        break;
                    }

                    Add(WireItem.Action(MavCmd.DoLandStart), i);
                    // p3 stays 0: PX4 rejects a parameter the command does not use unless it is 0 or NaN.
                    Add(WireItem.At(positionFrame, MavCmd.NavLoiterToAlt, center.Latitude, center.Longitude,
                        approachAltitude, param1: 1, param2: (float)item.Param2, param4: 1), i);
                    Add(WireItem.At(positionFrame, MavCmd.NavLand, touchdown.Latitude, touchdown.Longitude,
                        touchdownAltitude, param4: float.NaN), i);
                    break;

                case MissionItemType.LandingPattern:
                    blockers.Add($"item {i + 1}: the landing pattern has no touchdown point");
                    break;

                case MissionItemType.DoJump:
                    var target = (int)item.Param1;
                    if (target < 0 || target >= items.Count)
                    {
                        blockers.Add($"item {i + 1}: the jump has no target");
                        break;
                    }

                    Add(WireItem.Action(MavCmd.DoJump, firstSequence[target], (float)item.Param2, autoContinue: item.AutoContinue), i);
                    break;

                default:
                    var (command, frame) = WireOf(item.Type);
                    if (item.UsesLocation && item.Location is null)
                    {
                        blockers.Add($"item {i + 1}: {item.Type} has no position");
                        break;
                    }

                    if (item.Location is { } location)
                    {
                        if (Altitude(item.AltitudeMeters, location) is not { } altitude)
                        {
                            blockers.Add($"item {i + 1}: no terrain data at its position for an above-terrain altitude");
                            break;
                        }

                        Add(new WireItem(frame == MavFrame.GlobalRelativeAlt ? positionFrame : frame, command, item.AutoContinue,
                            (float)item.Param1, (float)item.Param2, (float)item.Param3, (float)item.Param4,
                            Degrees(location.Latitude), Degrees(location.Longitude), altitude), i);
                        break;
                    }

                    Add(new WireItem(frame, command, item.AutoContinue, (float)item.Param1, (float)item.Param2, (float)item.Param3, (float)item.Param4, 0, 0, 0), i);
                    break;
            }
        }

        if (mission.Settings.EndAction == MissionEndAction.ReturnToLaunch && !items.Any(item => item.Phase == MissionPhase.Landing))
        {
            Add(WireItem.Action(MavCmd.NavReturnToLaunch), -1);
        }

        if (wire.Count == offset)
        {
            blockers.Add("the plan is empty");
        }

        return new EncodedMission(wire, planIndex, blockers);
    }

    /// <summary>Geofence items. Polygons go before circles: PX4 reads every polygon after a circle as a circle.</summary>
    public static IReadOnlyList<WireItem> EncodeFences(IEnumerable<Fence> fences)
    {
        var list = fences.ToList();
        var wire = new List<WireItem>();

        foreach (var fence in list)
        {
            if (fence.Area is PolygonArea polygon)
            {
                var command = fence.Inclusion ? MavCmd.NavFencePolygonVertexInclusion : MavCmd.NavFencePolygonVertexExclusion;
                wire.AddRange(polygon.Vertices.Select(vertex =>
                    WireItem.At(MavFrame.Global, command, vertex.Latitude, vertex.Longitude, 0, param1: polygon.Vertices.Length, autoContinue: false)));
            }
        }

        foreach (var fence in list)
        {
            if (fence.Area is CircleArea circle)
            {
                wire.Add(WireItem.At(MavFrame.Global, fence.Inclusion ? MavCmd.NavFenceCircleInclusion : MavCmd.NavFenceCircleExclusion,
                    circle.Center.Latitude, circle.Center.Longitude, 0, param1: (float)circle.RadiusMeters, autoContinue: false));
            }
        }

        return wire;
    }

    private static int Degrees(double value) => (int)Math.Round(value * 1e7);
}
