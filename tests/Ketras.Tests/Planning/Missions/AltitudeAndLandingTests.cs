using Ketras.Mavlink.Messages;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;
using Ketras.Station.Missions;

namespace Ketras.Tests.Planning.Missions;

public sealed class AltitudeAndLandingTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);
    private static readonly GeoPoint Far = GeoMath.Offset(Home, 0, 1000);

    private static double? Ground(GeoPoint point) => point == Far ? 520 : 500;

    private static Mission Waypoint(AltitudeReference reference, double altitude)
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home, HomeAltitudeMeters = 500, AltitudeReference = reference });
        return MissionEditor.Add(mission, MissionItemType.Waypoint, Far, new MissionItemOptions { AltitudeMeters = altitude }, null).Mission;
    }

    [Theory]
    [InlineData(AltitudeReference.RelativeToHome, 100, MavFrame.GlobalRelativeAlt, 100)]
    [InlineData(AltitudeReference.AboveSeaLevel, 600, MavFrame.Global, 600)]
    [InlineData(AltitudeReference.AboveTerrain, 80, MavFrame.Global, 600)]
    public void Positions_go_out_in_the_frame_of_the_plans_reference(AltitudeReference reference, double altitude, MavFrame frame, float wire)
    {
        var encoded = MissionEncoder.Encode(Waypoint(reference, altitude), homeAtSequenceZero: false, Ground);

        var item = encoded.Items[0]; // the plan's return to launch follows
        Assert.Equal((frame, wire), (item.Frame, item.Z));
        Assert.Empty(encoded.Blockers);
    }

    [Fact]
    public void An_above_terrain_plan_without_terrain_is_not_uploaded()
    {
        var encoded = MissionEncoder.Encode(Waypoint(AltitudeReference.AboveTerrain, 80), homeAtSequenceZero: false, _ => null);

        Assert.Equal(["item 1: no terrain data at its position for an above-terrain altitude"], encoded.Blockers);
    }

    [Theory]
    [InlineData(AltitudeReference.RelativeToHome)]
    [InlineData(AltitudeReference.AboveSeaLevel)]
    [InlineData(AltitudeReference.AboveTerrain)]
    public void The_reference_and_the_landing_settings_survive_a_plan_file(AltitudeReference reference)
    {
        var mission = Waypoint(reference, 80);
        mission = MissionEditor.Add(mission, MissionItemType.LandingPattern, Home, new MissionItemOptions { AltitudeMeters = 40, HeadingDegrees = 90 }, null).Mission;
        mission = MissionEditor.UpdateSettings(mission, s => s with { Landing = new LandingSettings { TerrainUse = LandingTerrainUse.GlideSlope, AbortIfTerrainLost = true } });

        var read = PlanFileFormat.Read(PlanFileFormat.Write(new Plan { Mission = mission }, Ground));

        Assert.Empty(read.Skipped);
        var settings = read.Plan.Mission.Settings;
        Assert.Equal(reference, settings.AltitudeReference);
        Assert.Equal(mission.Settings.Landing, settings.Landing);
        Assert.Equal(80, read.Plan.Mission.Items[0].AltitudeMeters);
        Assert.Equal((40, 0), (read.Plan.Mission.Items[1].AltitudeMeters, read.Plan.Mission.Items[1].Param1), new ToleranceComparer());
    }

    [Fact]
    public void A_qgc_item_above_terrain_carries_its_altitude_and_its_sea_level_value()
    {
        var json = PlanFileFormat.Write(new Plan { Mission = Waypoint(AltitudeReference.AboveTerrain, 80) }, Ground);

        Assert.Contains("\"globalPlanAltitudeMode\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"AltitudeMode\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"AMSLAltAboveTerrain\": 600", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mission_that_lands_writes_the_plans_landing_settings_and_says_so()
    {
        await using var station = await StationFixture.StartAsync(parameters: new Dictionary<string, (MavParamType Type, double Value)>
        {
            ["FW_LND_USETER"] = (MavParamType.Int32, 1),
            ["FW_LND_ABORT"] = (MavParamType.Int32, 3)
        });
        await station.ConnectAsync();
        var plans = station.Get<PlanStore>();
        plans.UpdateSettings(s => s with { PlannedHome = Home, HomeAltitudeMeters = 500 });
        plans.AddItem(MissionItemType.Waypoint, Far);

        var withoutLanding = await station.Get<MissionUploadService>().UploadMissionAsync();
        Assert.DoesNotContain("landing:", withoutLanding.Message, StringComparison.Ordinal);
        Assert.Equal(3, station.Vehicle.Parameter("FW_LND_ABORT"));

        plans.AddItem(MissionItemType.LandingPattern, Home, new MissionItemOptions { HeadingDegrees = 90 });
        var result = await station.Get<MissionUploadService>().UploadMissionAsync();

        Assert.True(result.IsAccepted, result.Message);
        Assert.EndsWith("; landing: FW_LND_ABORT 3 → 0", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, station.Vehicle.Parameter("FW_LND_ABORT"));
        Assert.Equal(1, station.Vehicle.Parameter("FW_LND_USETER"));

        var again = await station.Get<MissionUploadService>().UploadMissionAsync();
        Assert.DoesNotContain("landing:", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_the_plans_reference_converts_it_and_an_item_without_terrain_stops_it()
    {
        var plans = new PlanStore(new Ketras.Station.Notifications.NotificationStore(TimeProvider.System));
        plans.Apply(_ => new Plan { Mission = Waypoint(AltitudeReference.RelativeToHome, 100) }, PlanChange.Mission);

        Assert.Null(plans.SetAltitudeReference(AltitudeReference.AboveTerrain, Ground));
        Assert.Equal((AltitudeReference.AboveTerrain, 80), (plans.Current.Mission.Settings.AltitudeReference, plans.Current.Mission.Items[0].AltitudeMeters));

        Assert.Equal(1, plans.SetAltitudeReference(AltitudeReference.RelativeToHome, _ => null));
        Assert.Equal(AltitudeReference.AboveTerrain, plans.Current.Mission.Settings.AltitudeReference);
    }

    private sealed class ToleranceComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 0.11 && Math.Abs(x.Item2 - y.Item2) < 0.11;

        public int GetHashCode((double, double) obj) => 0;
    }
}
