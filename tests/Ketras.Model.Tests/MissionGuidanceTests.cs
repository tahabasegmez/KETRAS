using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Vehicles;

namespace Ketras.Model.Tests;

public sealed class MissionGuidanceTests
{
    private static readonly GeoPoint Home = new(47.0, 8.0);
    private static readonly VehicleLimits Limits = new() { MinAirspeedMps = 12, MaxAirspeedMps = 25, MaxBankDegrees = 45, MaxLandingSlopeDegrees = 5, MaxClimbRateMps = 4, MaxSinkRateMps = 2 };

    private static Mission Plan(params (MissionItemType Type, GeoPoint? At, double Altitude)[] items)
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home, HomeAltitudeMeters = 400, CruiseSpeedMps = 20 });
        foreach (var (type, at, altitude) in items)
        {
            mission = MissionEditor.Add(mission, type, at, new MissionItemOptions { AltitudeMeters = altitude }, Limits).Mission;
        }

        return mission;
    }

    private static ParameterRange RangeOf(Mission mission, int index, string key, Func<GeoPoint, double?>? groundAt = null) =>
        MissionParameterRules.Range(mission, mission.Items[index], MissionItemParameters.For(mission.Items[index].Type).Single(p => p.Key == key), Limits, groundAt);

    [Fact]
    public void An_orbit_cannot_be_tighter_than_the_turn_at_the_airspeed_in_effect()
    {
        var mission = Plan((MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 500), 80), (MissionItemType.ChangeSpeed, null, 0),
            (MissionItemType.LoiterUnlimited, GeoMath.Offset(Home, 0, 2000), 120));
        mission = mission with { Items = mission.Items.SetItem(1, mission.Items[1] with { Param2 = 24 }) };

        var radius = RangeOf(mission, 2, "radius");

        // 24 m/s at 45° bank: 24² / 9.80665 ≈ 58.7 m.
        Assert.Equal((10, 5000, RangeReason.TurnRadius, 24.0), (radius.HardMinimum, radius.HardMaximum, radius.Reason, radius.ReasonValue));
        Assert.Equal(58.7, radius.SoftMinimum!.Value, 1);
        Assert.Null(radius.SoftMaximum);
        Assert.True(radius.IsOutsideSoft(40));
        Assert.False(radius.IsOutsideSoft(80));
    }

    [Fact]
    public void Airspeed_follows_the_vehicle_and_altitude_what_the_climb_reaches_over_the_leg()
    {
        var mission = Plan((MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 500), 100), (MissionItemType.ChangeSpeed, null, 0),
            (MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 1500), 300));

        var airspeed = RangeOf(mission, 1, "airspeed");
        var altitude = RangeOf(mission, 2, "altitude");

        Assert.Equal((12.0, 25.0, RangeReason.Airspeed), (airspeed.SoftMinimum, airspeed.SoftMaximum, airspeed.Reason));
        // 1000 m at 20 m/s is 50 s: 100 - 2·50 = 0 m up to 100 + 4·50 = 300 m, reached from item 1.
        Assert.Equal(0, altitude.SoftMinimum!.Value, 0);
        Assert.Equal(300, altitude.SoftMaximum!.Value, 0);
        Assert.Equal((RangeReason.ClimbFromPrevious, 1.0), (altitude.Reason, altitude.ReasonValue));
        Assert.Equal(RangeReason.None, RangeOf(mission, 0, "altitude").Reason);
    }

    [Fact]
    public void A_landing_approach_must_be_long_enough_for_the_slope_and_touchdown_on_the_ground()
    {
        var touchdown = GeoMath.Offset(Home, 180, 800);
        var mission = Plan((MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 500), 80), (MissionItemType.LandingPattern, touchdown, 70));

        var length = RangeOf(mission, 1, "approach-length", _ => 410);
        var ground = RangeOf(mission, 1, "touchdown-altitude", _ => 410);

        // 70 m above home over the 0 m touchdown at 5°: 70 / tan 5° ≈ 800 m.
        Assert.Equal(70 / Math.Tan(5 * Math.PI / 180), length.SoftMinimum!.Value, 3);
        Assert.Equal((RangeReason.GlideSlope, 5.0), (length.Reason, length.ReasonValue));
        Assert.Equal((10.0, 10.0, RangeReason.Ground), (ground.SoftMinimum, ground.SoftMaximum, ground.Reason));
    }

    [Fact]
    public void The_turn_circles_touch_the_item_across_the_incoming_leg_and_flag_a_next_item_inside_one()
    {
        var waypoint = GeoMath.Offset(Home, 0, 1500);
        var mission = Plan((MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 500), 100), (MissionItemType.Waypoint, waypoint, 100),
            (MissionItemType.Waypoint, GeoMath.Offset(waypoint, 90, 60), 100));

        var guides = MissionGuideRules.For(mission, mission.Items[1].Id, Limits, null);
        var turns = guides.Where(g => g.Reason == GuideReason.TightestTurn).ToList();

        // 20 m/s at 45°: about 40.8 m; the next item 60 m east lies inside the right-hand circle.
        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.Equal(40.8, t.RadiusMeters, 1));
        Assert.All(turns, t => Assert.Equal(t.RadiusMeters, GeoMath.DistanceMeters(t.Position, waypoint), 1));
        Assert.Equal([true, false], turns.Select(t => t.Warning));
        Assert.Empty(MissionGuideRules.For(mission, null, Limits, null));
    }

    [Fact]
    public void A_landing_pattern_shows_the_shortest_approach_and_its_handles_and_a_loiter_its_radius_handle()
    {
        var touchdown = GeoMath.Offset(Home, 180, 800);
        var mission = Plan((MissionItemType.Takeoff, GeoMath.Offset(Home, 0, 500), 80), (MissionItemType.LoiterUnlimited, GeoMath.Offset(Home, 0, 1500), 80),
            (MissionItemType.LandingPattern, touchdown, 70));

        var landing = MissionGuideRules.For(mission, mission.Items[2].Id, Limits, _ => 400);
        var loiter = MissionGuideRules.For(mission, mission.Items[1].Id, Limits, null);

        var approach = Assert.Single(landing, g => g.Reason == GuideReason.ShortestApproach);
        Assert.Equal((touchdown, 70 / Math.Tan(5 * Math.PI / 180)), (approach.Position, approach.RadiusMeters));
        Assert.Equal(MissionItemValues.LandingPatternApproachStart(mission.Items[2]), Assert.Single(landing, g => g.Reason == GuideReason.ApproachHandle).Position);
        Assert.Single(landing, g => g.Reason == GuideReason.SmallestOrbit);
        var handle = Assert.Single(loiter, g => g.Reason == GuideReason.RadiusHandle);
        Assert.Equal(MissionItemValues.LoiterRadiusMeters(mission.Items[1])!.Value, GeoMath.DistanceMeters(mission.Items[1].Location!.Value, handle.Position), 1);
        Assert.False(Assert.Single(loiter, g => g.Reason == GuideReason.SmallestOrbit).Warning);
    }
}
