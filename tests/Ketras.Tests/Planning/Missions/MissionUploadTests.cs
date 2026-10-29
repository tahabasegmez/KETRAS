using Ketras.Commands;
using Ketras.Environment;
using Ketras.Geo;
using Ketras.Mavlink.Messages;
using Ketras.Notifications;
using Ketras.Planning;
using Ketras.Planning.Fences;
using Ketras.Planning.Missions;
using Ketras.Vehicles;

namespace Ketras.Tests.Planning.Missions;

public sealed class MissionUploadTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private static void BuildPlan(PlanStore plans)
    {
        plans.UpdateSettings(s => s with { PlannedHome = Home, HomeAltitudeMeters = 488 });
        plans.AddItem(MissionItemType.Takeoff, GeoMath.Offset(Home, 90, 500), new MissionItemOptions { AltitudeMeters = 60 });
        var target = plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 1000));
        plans.AddItem(MissionItemType.LandingPattern, GeoMath.Offset(Home, 90, 50), new MissionItemOptions { HeadingDegrees = 90 });
        plans.AddItem(MissionItemType.DoJump, null, new MissionItemOptions { JumpTargetId = target, JumpRepeatCount = 2 });
        plans.UpsertFence(new Fence("area", new CircleArea(Home, 3000), Inclusion: true));
        plans.UpsertFence(new Fence("pad", new PolygonArea([GeoMath.Offset(Home, 180, 900), GeoMath.Offset(Home, 190, 1000), GeoMath.Offset(Home, 170, 1000)])));
    }

    [Fact]
    public async Task Without_a_vehicle_the_planned_home_follows_the_first_item_until_a_vehicle_reports_its_own()
    {
        await using var station = await StationFixture.StartAsync();
        var plans = station.Get<PlanStore>();
        var takeoff = GeoMath.Offset(Home, 90, 500);

        var id = plans.AddItem(MissionItemType.Takeoff, takeoff)!.Value;
        plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 1000));
        Assert.Equal(takeoff, plans.Current.Mission.Settings.PlannedHome);

        var moved = GeoMath.Offset(Home, 180, 200);
        plans.UpdateItem(id, item => item with { Location = moved });
        Assert.Equal(moved, plans.Current.Mission.Settings.PlannedHome);

        // A home the plan brought (e.g. from a file) stays.
        plans.UpdateSettings(s => s with { PlannedHome = Home });
        plans.UpdateItem(id, item => item with { Location = takeoff });
        Assert.Equal(Home, plans.Current.Mission.Settings.PlannedHome);

        await station.ConnectAsync();
        station.Vehicle.SendHome(47.38, 8.53, 490);
        await StationFixture.WaitForAsync(() => plans.Current.Mission.Settings.PlannedHome.Latitude == 47.38);
        plans.UpdateItem(id, item => item with { Location = moved });
        Assert.Equal(47.38, plans.Current.Mission.Settings.PlannedHome.Latitude);
    }

    [Fact]
    public async Task The_mission_uploads_expanded_with_wire_jump_targets_and_progress_maps_back_to_the_plan()
    {
        await using var station = await StationFixture.StartAsync();
        await station.ConnectAsync();
        var plans = station.Get<PlanStore>();
        BuildPlan(plans);
        var commands = station.Get<CommandService>();

        var result = await commands.ExecuteAsync(new CommandRequest(commands.Commands.Single(c => c.Id == "upload-mission")));

        Assert.True(result.IsAccepted, result.Message);
        var items = station.Vehicle.Items(MavMissionType.Mission);
        Assert.Equal([MavCmd.NavTakeoff, MavCmd.NavWaypoint, MavCmd.DoJump, MavCmd.DoLandStart, MavCmd.NavLoiterToAlt, MavCmd.NavLand], items.Select(i => i.Command));
        Assert.Equal(1, items[2].Param1); // the waypoint is wire sequence 1 (PX4: no home at 0)
        Assert.Equal(0, items[4].Param3);
        Assert.Equal(1, items[4].Param4);

        station.Vehicle.SendMissionCurrent(5, items.Count);
        await commands.ExecuteAsync(new CommandRequest(commands.Commands.Single(c => c.Id == "arm")));
        await commands.ExecuteAsync(new CommandRequest(commands.Commands.Single(c => c.Id == "mode-mission")));
        var tracker = station.Get<MissionProgressTracker>();
        await StationFixture.WaitForAsync(() => tracker.Progress.State == MissionExecution.Executing);
        Assert.Equal(3, tracker.Progress.CurrentItemIndex); // wire 5 is the landing pattern's NAV_LAND, plan item 3
    }

    [Fact]
    public async Task The_geofence_uploads_polygons_first_with_the_zones_marked_for_it()
    {
        await using var station = await StationFixture.StartAsync();
        await station.ConnectAsync();
        BuildPlan(station.Get<PlanStore>());
        station.Get<ManualEnvironmentProvider>().UpsertZone(new Zone
        {
            Id = "restricted", Source = "test", Area = new CircleArea(GeoMath.Offset(Home, 45, 2000), 200), IncludeInGeofence = true
        });
        station.Get<ManualEnvironmentProvider>().UpsertZone(new Zone
        {
            Id = "info", Source = "test", Area = new CircleArea(GeoMath.Offset(Home, 45, 2500), 200), Kind = ZoneKind.Advisory, IncludeInGeofence = true
        });

        var result = await station.Get<MissionUploadService>().UploadFencesAsync();

        Assert.True(result.IsAccepted, result.Message);
        var items = station.Vehicle.Items(MavMissionType.Fence);
        Assert.Equal([MavCmd.NavFencePolygonVertexExclusion, MavCmd.NavFencePolygonVertexExclusion, MavCmd.NavFencePolygonVertexExclusion,
            MavCmd.NavFenceCircleInclusion, MavCmd.NavFenceCircleExclusion], items.Select(i => i.Command));
        Assert.Equal("geofence uploaded: 3 zones", result.Message);
    }

    [Fact]
    public async Task Without_a_vehicle_the_upload_is_refused_and_an_empty_plan_clears_the_vehicle()
    {
        await using var station = await StationFixture.StartAsync();
        Assert.Equal("no vehicle", (await station.Get<MissionUploadService>().UploadMissionAsync()).Message);

        await station.ConnectAsync();
        BuildPlan(station.Get<PlanStore>());
        Assert.True((await station.Get<MissionUploadService>().UploadMissionAsync()).IsAccepted);
        station.Get<PlanStore>().ClearMission();
        var result = await station.Get<MissionUploadService>().UploadMissionAsync();

        Assert.Equal("vehicle mission cleared", result.Message);
        Assert.Empty(station.Vehicle.Items(MavMissionType.Mission));
    }

    [Fact]
    public async Task Plan_findings_become_conditions_and_are_withdrawn_when_fixed()
    {
        await using var station = await StationFixture.StartAsync();
        var plans = station.Get<PlanStore>();
        var notifications = station.Get<NotificationStore>();
        plans.UpdateSettings(s => s with { PlannedHome = Home });
        plans.UpsertFence(new Fence("keep-out", new CircleArea(Home, 100)));

        await StationFixture.WaitForAsync(() => notifications.Conditions.Any(n => n.Source == PlanStore.Source && n.Key!.StartsWith("fences.home-inside", StringComparison.Ordinal)));
        plans.RemoveFence("keep-out");
        await StationFixture.WaitForAsync(() => notifications.Conditions.All(n => n.Source != PlanStore.Source));
    }

    [Fact]
    public async Task A_refused_edit_is_reported_and_leaves_the_plan_unchanged()
    {
        await using var station = await StationFixture.StartAsync();
        var plans = station.Get<PlanStore>();
        plans.AddItem(MissionItemType.Takeoff, Home);
        var before = plans.Current;

        Assert.Null(plans.AddItem(MissionItemType.Takeoff, Home));
        Assert.Same(before, plans.Current);
        Assert.Contains(station.Get<NotificationStore>().History, n => n.Title == "The plan already has a takeoff.");
    }

    [Fact]
    public void A_plan_survives_a_qgc_plan_file_round_trip()
    {
        var plans = new PlanStore(new NotificationStore(TimeProvider.System));
        BuildPlan(plans);
        var original = plans.Current;

        var json = PlanFileFormat.Write(original);
        var read = PlanFileFormat.Read(json);

        Assert.Empty(read.Skipped);
        var mission = read.Plan.Mission;
        Assert.Equal(original.Mission.Settings.PlannedHome, mission.Settings.PlannedHome);
        Assert.Equal(original.Mission.Items.Select(i => i.Type), mission.Items.Select(i => i.Type));
        var jump = mission.Items.Single(i => i.Type == MissionItemType.DoJump);
        Assert.Equal(mission.Items[1].Id, jump.JumpTargetId);
        Assert.Equal(2, jump.Param2);
        var landing = mission.Items.Single(i => i.Type == MissionItemType.LandingPattern);
        var expected = original.Mission.Items.Single(i => i.Type == MissionItemType.LandingPattern);
        Assert.Equal(expected.Param3, landing.Param3, 0);
        Assert.Equal(expected.Param4, landing.Param4, 0);
        Assert.Equal(expected.Param2, landing.Param2, 6);
        Assert.Equal(expected.AltitudeMeters, landing.AltitudeMeters);
        Assert.True(double.IsNaN(mission.Items[1].Param4));
        // QGC's file keeps polygons and circles in separate lists, so polygons come back first.
        Assert.Equal(original.Fences.OrderBy(f => f.Area is CircleArea).Select(f => (f.Area.GetType(), f.Inclusion)), read.Plan.Fences.Select(f => (f.Area.GetType(), f.Inclusion)));
        Assert.Contains("\"fileType\": \"Plan\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_plan_is_refused_and_unsupported_parts_are_listed()
    {
        Assert.Throws<FormatException>(() => PlanFileFormat.Read("{\"fileType\":\"GeoFence\"}"));
        Assert.Throws<FormatException>(() => PlanFileFormat.Read("not json"));

        var read = PlanFileFormat.Read("""
            {"fileType":"Plan","groundStation":"QGroundControl","version":1,
             "mission":{"version":2,"plannedHomePosition":[47,8,400],"items":[
               {"type":"ComplexItem","complexItemType":"survey"},
               {"type":"SimpleItem","command":16,"frame":3,"params":[0,0,0,null,47.1,8.1,50],"doJumpId":1,"autoContinue":true},
               {"type":"SimpleItem","command":3000,"frame":2,"params":[0,0,0,0,0,0,0],"doJumpId":2}]},
             "rallyPoints":{"points":[[47,8,50]],"version":2}}
            """);

        Assert.Single(read.Plan.Mission.Items);
        Assert.Equal(3, read.Skipped.Count);
        Assert.Equal(new GeoPoint(47.1, 8.1), read.Plan.Mission.Items[0].Location);
        Assert.Equal(MissionEndAction.Hold, read.Plan.Mission.Settings.EndAction);
    }

    [Theory]
    [InlineData(MissionEndAction.ReturnToLaunch)]
    [InlineData(MissionEndAction.Hold)]
    public void The_end_action_survives_a_plan_file_without_becoming_an_item(MissionEndAction endAction)
    {
        var plans = new PlanStore(new NotificationStore(TimeProvider.System));
        plans.UpdateSettings(s => s with { PlannedHome = new GeoPoint(47, 8), EndAction = endAction });
        plans.AddItem(MissionItemType.Takeoff, new GeoPoint(47, 8));
        plans.AddItem(MissionItemType.Waypoint, new GeoPoint(47.01, 8));

        var mission = PlanFileFormat.Read(PlanFileFormat.Write(plans.Current)).Plan.Mission;

        Assert.Equal([MissionItemType.Takeoff, MissionItemType.Waypoint], mission.Items.Select(i => i.Type));
        Assert.Equal(endAction, mission.Settings.EndAction);
    }
}
