using Ketras.Model.Geo;
using Ketras.Model.Vehicles;

namespace Ketras.Model.Missions;

/// <summary>How a guide is drawn.</summary>
public enum GuideShape
{
    /// <summary>A circle of <see cref="MissionGuide.RadiusMeters"/> around <see cref="MissionGuide.Position"/>.</summary>
    Ring,

    /// <summary>A point on the route.</summary>
    Mark,

    /// <summary>A point the operator can drag.</summary>
    Handle
}

/// <summary>What a guide shows; its <see cref="MissionGuide.Value"/> is the figure the reason names.</summary>
public enum GuideReason
{
    /// <summary>The tightest turn out of the item along the incoming leg (radius in metres).</summary>
    TightestTurn,

    /// <summary>The smallest orbit the aircraft can fly (radius in metres).</summary>
    SmallestOrbit,

    /// <summary>The shortest landing approach the glide slope allows; the approach start lies outside it (length in metres).</summary>
    ShortestApproach,

    /// <summary>The shortest glide to a land point from the previous altitude (distance in metres).</summary>
    ShortestGlide,

    /// <summary>Where the item's altitude is reached on the incoming leg (that altitude, in the plan's reference).</summary>
    AltitudeReached,

    /// <summary>Drag to change an orbit's radius (the radius in metres).</summary>
    RadiusHandle,

    /// <summary>Drag to swing and stretch a landing approach (its length in metres).</summary>
    ApproachHandle
}

/// <summary>One drawing of the guide layer.</summary>
/// <param name="Shape">How it is drawn.</param>
/// <param name="Reason">What it shows.</param>
/// <param name="Position">Its point, or a ring's centre.</param>
/// <param name="RadiusMeters">A ring's radius; 0 otherwise.</param>
/// <param name="Value">The figure <paramref name="Reason"/> names.</param>
/// <param name="Warning">The plan, as it is, breaks this limit.</param>
public sealed record MissionGuide(GuideShape Shape, GuideReason Reason, GeoPoint Position, double RadiusMeters, double Value, bool Warning);

/// <summary>
/// Pure rules: the guide drawings of the selected item, showing where the vehicle's limits fall around it and what can
/// be dragged. They use the same figures as <see cref="MissionRules.Validate"/> and <see cref="MissionParameterRules"/>.
/// </summary>
public static class MissionGuideRules
{
    /// <summary>The guides of item <paramref name="selected"/>; empty when nothing located is selected.</summary>
    public static IReadOnlyList<MissionGuide> For(Mission mission, Guid? selected, VehicleLimits limits, Func<GeoPoint, double?>? groundAt)
    {
        var index = selected is { } id ? mission.IndexOf(id) : -1;
        if (index < 0 || mission.Items[index] is not { } item || item.Location is not { } location)
        {
            return [];
        }

        var guides = new List<MissionGuide>();
        var settings = mission.Settings;
        var speed = Math.Clamp(MissionParameterRules.SpeedAt(mission, index), limits.MinAirspeedMps, limits.MaxAirspeedMps);
        var turn = MissionRules.MinimumTurnRadiusMeters(speed, limits.MaxBankDegrees);
        var from = Previous(mission, index, groundAt);

        if (item.Type is MissionItemType.Takeoff or MissionItemType.Waypoint or MissionItemType.Land && from is { } incoming)
        {
            AddTurnCircles(guides, mission, index, location, incoming.Position, turn);
        }

        if (from is { } leg && MissionAltitudes.AmslOf(settings, item, groundAt) is { } target && leg.Amsl is { } start)
        {
            var legMeters = GeoMath.DistanceMeters(leg.Position, location);
            var rate = target >= start ? limits.MaxClimbRateMps : limits.MaxSinkRateMps;
            var needed = Math.Abs(target - start) / Math.Max(rate, 0.1) * speed;
            if (needed > 1 && legMeters > 1 && item.Type != MissionItemType.LandingPattern)
            {
                var along = Math.Min(needed, legMeters);
                guides.Add(new MissionGuide(GuideShape.Mark, GuideReason.AltitudeReached,
                    GeoMath.Offset(leg.Position, GeoMath.BearingDegrees(leg.Position, location), along), 0, item.AltitudeMeters, needed > legMeters));
            }
        }

        switch (item.Type)
        {
            case MissionItemType.LoiterUnlimited or MissionItemType.LoiterTime or MissionItemType.LoiterToAltitude
                when MissionItemValues.LoiterRadiusMeters(item) is { } radius:
                guides.Add(new MissionGuide(GuideShape.Ring, GuideReason.SmallestOrbit, location, turn, turn, radius < turn));
                guides.Add(new MissionGuide(GuideShape.Handle, GuideReason.RadiusHandle, RadiusHandle(location, radius), 0, radius, false));
                break;

            case MissionItemType.LandingPattern:
                if (MissionRules.LandingDrop(settings, item, groundAt) is { } drop and > 0)
                {
                    var shortest = drop / Math.Tan(limits.MaxLandingSlopeDegrees * Math.PI / 180);
                    guides.Add(new MissionGuide(GuideShape.Ring, GuideReason.ShortestApproach, location, shortest, shortest, item.Param3 < shortest));
                }

                if (MissionItemValues.LandingPatternOrbitCenter(item) is { } center)
                {
                    var radius = Math.Abs(item.Param2);
                    guides.Add(new MissionGuide(GuideShape.Ring, GuideReason.SmallestOrbit, center, turn, turn, radius < turn));
                }

                if (MissionItemValues.LandingPatternApproachStart(item) is { } approach)
                {
                    guides.Add(new MissionGuide(GuideShape.Handle, GuideReason.ApproachHandle, approach, 0, item.Param3, false));
                }

                break;

            case MissionItemType.Land when from is { Amsl: { } high } && MissionAltitudes.TouchdownAmslOf(settings, item, groundAt) is { } low && high > low:
            {
                var shortest = (high - low) / Math.Tan(limits.MaxLandingSlopeDegrees * Math.PI / 180);
                guides.Add(new MissionGuide(GuideShape.Ring, GuideReason.ShortestGlide, location, shortest, shortest,
                    GeoMath.DistanceMeters(from.Value.Position, location) < shortest));
                break;
            }
        }

        return guides;
    }

    /// <summary>Where an orbit's radius handle sits: due east of the centre, on the orbit.</summary>
    public static GeoPoint RadiusHandle(GeoPoint center, double radiusMeters) => GeoMath.Offset(center, 90, radiusMeters);

    /// <summary>The point the vehicle comes from: the previous located item, else the planned home; with its height AMSL.</summary>
    private static (GeoPoint Position, double? Amsl)? Previous(Mission mission, int index, Func<GeoPoint, double?>? groundAt)
    {
        var settings = mission.Settings;
        if (MissionParameterRules.PreviousLocated(mission, index) is { } previous && MissionAltitudes.PositionOf(previous.Item) is { } position)
        {
            return (position, MissionAltitudes.AmslOf(settings, previous.Item, groundAt));
        }

        return settings.PlannedHome.IsUnset ? null : (settings.PlannedHome, settings.HomeAltitudeMeters);
    }

    /// <summary>The tightest left and right turns out of the item; a ring holding the next item cannot reach it directly.</summary>
    private static void AddTurnCircles(List<MissionGuide> guides, Mission mission, int index, GeoPoint location, GeoPoint previous, double radius)
    {
        if (GeoMath.DistanceMeters(previous, location) < 1)
        {
            return;
        }

        var heading = GeoMath.BearingDegrees(previous, location);
        GeoPoint? next = null;
        for (var i = index + 1; i < mission.Items.Count && next is null; i++)
        {
            next = MissionAltitudes.PositionOf(mission.Items[i]);
        }

        foreach (var side in new[] { 90.0, 270.0 })
        {
            var center = GeoMath.Offset(location, (heading + side) % 360, radius);
            var blocked = next is { } point && GeoMath.DistanceMeters(center, point) < radius;
            guides.Add(new MissionGuide(GuideShape.Ring, GuideReason.TightestTurn, center, radius, radius, blocked));
        }
    }
}
