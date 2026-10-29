using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Model.Vehicles;

namespace Ketras.Tests.Planning.Missions;

public sealed class MissionTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private static Mission WithHome() => Mission.Empty with { Settings = new MissionSettings { PlannedHome = Home, HomeAltitudeMeters = 490 } };

    private static Mission Add(Mission mission, MissionItemType type, double bearing = 0, double distance = 0, MissionItemOptions? options = null)
    {
        var edit = MissionEditor.Add(mission, type, GeoMath.Offset(Home, bearing, distance), options);
        Assert.True(edit.Applied, edit.Reason);
        return edit.Mission;
    }

    [Fact]
    public void New_items_get_the_fixed_wing_defaults()
    {
        var limits = VehicleLimits.Default with { DefaultLoiterRadiusMeters = 120 };
        var settings = new MissionSettings { CruiseSpeedMps = 16, DefaultAltitudeMeters = 90 };

        var waypoint = MissionItemFactory.Create(MissionItemType.Waypoint, Home, settings: settings, limits: limits);
        var loiter = MissionItemFactory.Create(MissionItemType.LoiterTime, Home, settings: settings, limits: limits);
        var speed = MissionItemFactory.Create(MissionItemType.ChangeSpeed, settings: settings, limits: limits);
        var landing = MissionItemFactory.Create(MissionItemType.LandingPattern, Home, settings: settings, limits: limits);
        var photo = MissionItemFactory.Create(MissionItemType.TakePhoto, Home);

        Assert.True(double.IsNaN(waypoint.Param4));
        Assert.Equal(90, waypoint.AltitudeMeters);
        Assert.Equal(120, loiter.Param3);
        Assert.Equal(30, loiter.Param1);
        Assert.Equal(16, speed.Param2);
        Assert.Equal(-1, speed.Param3);
        Assert.Equal(40, landing.AltitudeMeters);
        Assert.Equal(460, landing.Param3); // 40 m at 5° needs 457 m, rounded up to 460
        Assert.Null(photo.Location);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_landing_orbit_is_tangent_to_the_approach_on_the_side_of_its_direction(bool clockwise)
    {
        var item = MissionItemFactory.Create(MissionItemType.LandingPattern, Home, new MissionItemOptions { HeadingDegrees = 90, DistanceMeters = 500 })
            with { Param2 = clockwise ? 100 : -100 };

        var start = MissionItemValues.LandingPatternApproachStart(item)!.Value;
        var center = MissionItemValues.LandingPatternOrbitCenter(item)!.Value;

        Assert.Equal(500, GeoMath.DistanceMeters(Home, start), 0);
        Assert.Equal(270, GeoMath.BearingDegrees(Home, start), 0);
        Assert.Equal(Math.Sqrt((500 * 500) + (100 * 100)), GeoMath.DistanceMeters(Home, center), 0);
        // Approaching eastwards, the right-hand side is south.
        Assert.Equal(clockwise ? 180 : 0, GeoMath.BearingDegrees(start, center), 0);
        Assert.Equal(Math.Atan2(40, 500) * 180 / Math.PI, MissionItemValues.LandingGlideSlopeDegrees(item)!.Value, 6);
    }

    [Fact]
    public void Items_go_to_the_end_of_their_phase_and_a_second_takeoff_is_refused()
    {
        var mission = Add(WithHome(), MissionItemType.Land, 90, 100);
        mission = Add(mission, MissionItemType.Takeoff, 90, 300);
        mission = Add(mission, MissionItemType.Waypoint, 0, 800);
        mission = Add(mission, MissionItemType.ChangeSpeed);

        var refused = MissionEditor.Add(mission, MissionItemType.Takeoff, Home);

        Assert.Equal([MissionItemType.Takeoff, MissionItemType.Waypoint, MissionItemType.ChangeSpeed, MissionItemType.Land],
            mission.Items.Select(item => item.Type));
        Assert.False(refused.Applied);
        Assert.Same(mission, refused.Mission);
        Assert.Equal("The plan already has a takeoff.", refused.Reason);
    }

    [Fact]
    public void An_item_moves_within_its_phase_only()
    {
        var mission = Add(Add(Add(WithHome(), MissionItemType.Takeoff, 90, 300), MissionItemType.Waypoint, 0, 800), MissionItemType.Waypoint, 45, 900);
        var second = mission.Items[2].Id;

        var moved = MissionEditor.Move(mission, second, -1);
        var acrossPhases = MissionEditor.Move(moved.Mission, second, -1);

        Assert.True(moved.Applied);
        Assert.Equal(1, moved.Mission.IndexOf(second));
        Assert.False(acrossPhases.Applied);
    }

    [Fact]
    public void Jump_targets_follow_their_item_through_edits_and_are_resolved_on_import()
    {
        var mission = Add(Add(WithHome(), MissionItemType.Takeoff, 90, 300), MissionItemType.Waypoint, 0, 800);
        var target = mission.Items[1].Id;
        mission = Add(mission, MissionItemType.DoJump, options: new MissionItemOptions { JumpTargetId = target });
        Assert.Equal(1, mission.Items[2].Param1);

        mission = Add(mission, MissionItemType.Waypoint, 30, 500);
        mission = MissionEditor.Move(mission, mission.Items[3].Id, -2).Mission;
        var jump = mission.Items.Single(item => item.Type == MissionItemType.DoJump);
        Assert.Equal(mission.IndexOf(target), (int)jump.Param1);

        mission = MissionEditor.Remove(mission, target).Mission;
        Assert.Equal(-1, mission.Items.Single(item => item.Type == MissionItemType.DoJump).Param1);

        var imported = Mission.Empty with
        {
            Items = [MissionItemFactory.Create(MissionItemType.Waypoint, Home), new MissionItem { Type = MissionItemType.DoJump, Param1 = 0, Param2 = 2 }]
        };
        var normalized = MissionEditor.Normalize(imported);
        Assert.Equal(normalized.Items[0].Id, normalized.Items[1].JumpTargetId);
        Assert.Contains(MissionRules.Validate(mission), finding => finding.Code == "mission.jump-target");
    }

    [Fact]
    public void Plans_outside_the_vehicles_limits_are_reported()
    {
        var limits = VehicleLimits.Default with { MaxAirspeedMps = 22, MaxBankDegrees = 30 };
        var mission = Add(WithHome(), MissionItemType.Takeoff, 90, 300);
        mission = Add(mission, MissionItemType.ChangeSpeed, options: new MissionItemOptions { SpeedMps = 30 });
        mission = Add(mission, MissionItemType.LoiterUnlimited, 0, 800, new MissionItemOptions { LoiterRadiusMeters = 40 });
        mission = Add(mission, MissionItemType.LandingPattern, 90, 50, new MissionItemOptions { DistanceMeters = 200 });

        var codes = MissionRules.Validate(mission, limits).Select(finding => finding.Code).ToList();

        Assert.Contains("mission.airspeed", codes);
        Assert.Contains("mission.turn-radius", codes); // 22 m/s at 30° needs about 85 m
        Assert.Contains("mission.landing-slope", codes); // 40 m over 200 m is 11°
        Assert.Equal(85.5, MissionRules.MinimumTurnRadiusMeters(22, 30), 1);
    }

    [Fact]
    public void A_plain_land_is_checked_against_the_item_before_it()
    {
        var mission = Add(WithHome(), MissionItemType.Waypoint, 270, 300, new MissionItemOptions { AltitudeMeters = 60 });
        mission = Add(mission, MissionItemType.Land, 90, 10, new MissionItemOptions { AltitudeMeters = 0 });

        var findings = MissionRules.Validate(mission);

        Assert.Contains(findings, finding => finding.Code == "mission.land-slope" && finding.ItemNumber == 2);
    }

    [Fact]
    public void Orbit_direction_and_radius_are_written_without_losing_each_other()
    {
        var item = MissionItemFactory.Create(MissionItemType.LandingPattern, Home);

        var ccw = MissionItemParameters.Write(item, MissionItemField.OrbitDirection, -1);
        var wider = MissionItemParameters.Write(ccw, MissionItemField.OrbitRadius, 150);

        Assert.Equal(-150, wider.Param2);
        Assert.Equal(-1, MissionItemParameters.Read(wider, MissionItemField.OrbitDirection));
        Assert.Equal(150, MissionItemParameters.Read(wider, MissionItemField.OrbitRadius));
        Assert.All(Enum.GetValues<MissionItemType>(), type => Assert.NotNull(MissionItemParameters.For(type)));
    }

    [Fact]
    public void A_well_formed_plan_has_no_mission_findings()
    {
        var mission = Add(WithHome(), MissionItemType.Takeoff, 90, 300, new MissionItemOptions { AltitudeMeters = 60 });
        mission = Add(mission, MissionItemType.Waypoint, 0, 1200);
        mission = Add(mission, MissionItemType.LandingPattern, 90, 50);

        Assert.Empty(PlanRules.Validate(new Plan { Mission = mission }));
    }
}
