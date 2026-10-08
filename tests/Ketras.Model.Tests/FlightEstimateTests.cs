using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Vehicles;

namespace Ketras.Model.Tests;

public sealed class FlightEstimateTests
{
    private static readonly GeoPoint Home = new(47.4, 8.5);

    private static Mission Plan()
    {
        var mission = MissionEditor.UpdateSettings(new Mission(), s => s with { PlannedHome = Home, CruiseSpeedMps = 20 });
        var north = GeoMath.Offset(Home, 0, 2000);
        mission = MissionEditor.Add(mission, MissionItemType.Takeoff, Home, new MissionItemOptions(), null).Mission;
        mission = MissionEditor.Add(mission, MissionItemType.Waypoint, north, new MissionItemOptions(), null).Mission;
        mission = MissionEditor.Add(mission, MissionItemType.ChangeSpeed, null, new MissionItemOptions(), null).Mission;
        mission = MissionEditor.Update(mission, mission.Items[2].Id, item => item with { Param2 = 25 }).Mission;
        mission = MissionEditor.Add(mission, MissionItemType.LoiterTime, GeoMath.Offset(north, 90, 1000), new MissionItemOptions(), null).Mission;
        return MissionEditor.Update(mission, mission.Items[3].Id, item => item with { Param1 = 30 }).Mission;
    }

    [Fact]
    public void The_estimate_flies_each_leg_at_the_speed_in_force_waits_and_returns_home()
    {
        var estimate = MissionTimeCalculator.Estimate(Plan());

        // 2000 m at 20 m/s, then 1000 m at 25 m/s and 30 s of loiter, then home over √(2000² + 1000²) at 25 m/s.
        var home = Math.Sqrt((2000 * 2000) + (1000 * 1000));
        Assert.Equal(2000 + 1000 + home, estimate.DistanceMeters, 0);
        Assert.Equal(100 + 40 + 30 + (home / 25), estimate.Seconds, 0);
        Assert.Equal(-1, estimate.Legs[^1].ItemIndex);
        Assert.Equal(30 + 40 + (home / 25), estimate.SecondsFrom(3), 0);
        Assert.False(estimate.IsOpenEnded);
        Assert.Null(estimate.StrongestHeadwind);
    }

    [Fact]
    public void Wind_changes_the_ground_speed_by_the_wind_triangle()
    {
        Assert.Equal((15.0, 0.25), MissionTimeCalculator.GroundSpeed(20, 0, windNorthMps: -5, windEastMps: 0));
        Assert.Equal((25.0, 0.0), MissionTimeCalculator.GroundSpeed(20, 0, windNorthMps: 5, windEastMps: 0));
        var (crossing, headwind) = MissionTimeCalculator.GroundSpeed(20, 0, windNorthMps: 0, windEastMps: 12);
        Assert.Equal((16.0, 0.0), (Math.Round(crossing, 6), headwind));

        // A 17 m/s north wind against the 20 m/s first leg: 85 % headwind there, a finding.
        var estimate = MissionTimeCalculator.Estimate(Plan(), windNorthMps: -17);
        Assert.Equal(1, estimate.StrongestHeadwind!.ItemIndex);
        var finding = Assert.Single(MissionBudgetRules.Validate(estimate, null));
        Assert.Equal(("mission.headwind", 2), (finding.Code, finding.ItemNumber));
    }

    [Fact]
    public void The_energy_budget_keeps_the_low_battery_reserve_and_says_when_it_does_not_last()
    {
        var budget = EnergyCalculator.Budget(5000, 100, 0.15, 1800, 10, CurrentSource.Entered)!;
        Assert.Equal((4250.0, 5000.0, -750.0), (budget.UsableMah, budget.NeededMah, budget.MarginMah));
        Assert.Equal(1530, budget.EnduranceSeconds, 6);
        Assert.Null(EnergyCalculator.Budget(0, 100, 0.15, 1800, 10, CurrentSource.Entered));
        Assert.Contains("does not last", Assert.Single(MissionBudgetRules.Validate(new MissionEstimate([], false), budget)).Message, StringComparison.Ordinal);

        var ample = EnergyCalculator.Budget(10000, 90, 0.15, 900, 10, CurrentSource.Measured)!;
        Assert.Equal(7500 - 2500, ample.MarginMah, 6);
        Assert.Empty(MissionBudgetRules.Validate(new MissionEstimate([], false), ample));

        Assert.Equal(10, EnergyCalculator.AverageCurrent(1000, 1500, TimeSpan.FromMinutes(3))!.Value, 6);
        Assert.Null(EnergyCalculator.AverageCurrent(1000, 1100, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Health_rules_grade_gps_estimator_vibration_battery_and_link()
    {
        var snapshot = new VehicleSnapshot
        {
            Id = new VehicleId(1), Fix = PositionFix.Fix3D, Satellites = 5, Hdop = 0.9,
            Estimator = new EstimatorHealth(0.1, 1.2, 0.2, 0.3, 0, 0), Vibration = new VibrationState(35, 4),
            Battery = new BatteryState(14.2, 1, null, null, 1)
        };
        var checks = FlightHealthRules.Evaluate(snapshot, new LinkQuality(12, null, null), new BatteryLimits(4, 3.5, 4.2), newClipping: 0)
            .ToDictionary(c => c.Code);

        Assert.Equal(CheckLevel.Warning, checks["vehicle.gps"].Level);
        Assert.Equal((CheckLevel.Error, "worst test ratio 1.20 (horizontal position)"),
            (checks["vehicle.estimator"].Level, checks["vehicle.estimator"].Detail.Replace(',', '.')));
        Assert.Equal(CheckLevel.Warning, checks["vehicle.vibration"].Level);
        Assert.Equal(CheckLevel.Warning, checks["vehicle.battery"].Level); // 3.55 V per cell, under a quarter of 3.5…4.2
        Assert.Equal(CheckLevel.Warning, checks["vehicle.link"].Level);

        var healthy = snapshot with
        {
            Satellites = 14, Estimator = new EstimatorHealth(0.1, 0.2, 0.2, 0.3, 0, 0), Vibration = new VibrationState(10, 4),
            Battery = new BatteryState(16.4, 1, null, null, 1)
        };
        Assert.All(FlightHealthRules.Evaluate(healthy, new LinkQuality(1, 180, 170), new BatteryLimits(4, 3.5, 4.2), 0), c => Assert.Equal(CheckLevel.Pass, c.Level));
        Assert.Equal(CheckLevel.Warning, FlightHealthRules.Evaluate(healthy, null, null, newClipping: 2).Single(c => c.Code == "vehicle.vibration").Level);
        Assert.All(FlightHealthRules.Evaluate(new VehicleSnapshot { Id = new VehicleId(1) }, null, null, 0), c => Assert.Equal(CheckLevel.Unknown, c.Level));
    }
}
