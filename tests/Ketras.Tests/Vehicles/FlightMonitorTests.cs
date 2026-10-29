using Ketras.Comms;
using Ketras.Geo;
using Ketras.Mavlink.Messages;
using Ketras.Notifications;
using Ketras.Planning;
using Ketras.Planning.Missions;
using Ketras.Settings;
using Ketras.Vehicles;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Tests.Vehicles;

public sealed class FlightMonitorTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private static readonly Dictionary<string, (MavParamType Type, double Value)> Battery = new()
    {
        ["BAT1_CAPACITY"] = (MavParamType.Real32, 5000),
        ["BAT_LOW_THR"] = (MavParamType.Real32, 0.15),
        ["BAT1_N_CELLS"] = (MavParamType.Int32, 4),
        ["BAT1_V_EMPTY"] = (MavParamType.Real32, 3.5),
        ["BAT1_V_CHARGED"] = (MavParamType.Real32, 4.2)
    };

    /// <summary>The monitor's clock, moved by hand; everything else keeps real time.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    private static Task<StationFixture> StartAsync(ManualTime time) => StationFixture.StartAsync(parameters: Battery, register: services =>
        services.AddSingleton(provider => new FlightMonitorWorker(provider.GetRequiredService<VehicleRegistry>(), provider.GetRequiredService<ConnectionService>(),
            provider.GetRequiredService<PlanStore>(), provider.GetRequiredService<PlanValidationService>(), provider.GetRequiredService<MissionProgressTracker>(),
            provider.GetRequiredService<NotificationStore>(), provider.GetRequiredService<SettingsStore>(), time)));

    [Fact]
    public async Task The_snapshot_carries_throttle_navigation_battery_gps_quality_estimator_vibration_and_the_arming_time()
    {
        await using var station = await StationFixture.StartAsync();
        var vehicle = await station.ConnectAsync();

        station.Vehicle.SendHud(17, 1, 64);
        station.Vehicle.SendPosition(47.39, 8.54, 520, 30);
        station.Vehicle.SendNavigation(640, 90, 12, -1, 8);
        station.Vehicle.SendBattery(16.4, 12.3, 72, consumedMah: 1450);
        station.Vehicle.SendGps(3, 14, 0.8);
        station.Vehicle.SendEstimator(0.2, 0.4);
        station.Vehicle.SendVibration(18);
        await vehicle.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);
        await StationFixture.WaitForAsync(() => vehicle.Snapshot is { ThrottlePercent: not null, Navigation: not null, Battery: not null, Estimator: not null, Vibration: not null, ArmedSinceUtc: not null });

        var snapshot = vehicle.Snapshot;
        Assert.Equal(64, snapshot.ThrottlePercent);
        Assert.Equal(new NavigationState(640, 90, 12, -1, 8), snapshot.Navigation);
        Assert.Equal((16.4, 12.3, 1450.0), (snapshot.Battery!.VoltageVolts!.Value, snapshot.Battery.CurrentAmps!.Value, snapshot.Battery.ConsumedMah!.Value));
        Assert.Equal(0.8, snapshot.Hdop);
        Assert.Equal(0.4, snapshot.Estimator!.Worst.Ratio, 6);
        Assert.Equal(18, snapshot.Vibration!.LevelMps2);
    }

    [Fact]
    public async Task Health_problems_become_conditions_and_clear_when_they_go()
    {
        var time = new ManualTime();
        await using var station = await StartAsync(time);
        var vehicle = await station.ConnectAsync();
        var monitor = station.Get<FlightMonitorWorker>();
        var notifications = station.Get<NotificationStore>();

        station.Vehicle.SendGps(3, 14, 0.8);
        station.Vehicle.SendVibration(70);
        station.Vehicle.SendBattery(13.8, 1, 40);
        await StationFixture.WaitForAsync(() => vehicle.Snapshot is { Vibration: not null, Battery: not null, Satellites: 14 });
        monitor.Tick();

        var checks = monitor.Status.Checks.ToDictionary(c => c.Code);
        Assert.Equal((CheckLevel.Pass, CheckLevel.Error, CheckLevel.Error), (checks["vehicle.gps"].Level, checks["vehicle.vibration"].Level, checks["vehicle.battery"].Level));
        Assert.Contains(notifications.Conditions, n => n.Source == FlightMonitorWorker.Source && n.Title == "Vibration is too high" && n.Severity == NotificationSeverity.Error);
        Assert.Contains(notifications.Conditions, n => n.Source == FlightMonitorWorker.Source && n.Title == "Battery is empty");

        station.Vehicle.SendVibration(10);
        station.Vehicle.SendBattery(16.6, 1, 95);
        await StationFixture.WaitForAsync(() => vehicle.Snapshot.Vibration?.LevelMps2 == 10 && vehicle.Snapshot.Battery?.VoltageVolts > 16);
        monitor.Tick();
        Assert.DoesNotContain(notifications.Conditions, n => n.Source == FlightMonitorWorker.Source);
    }

    [Fact]
    public async Task In_flight_errors_count_once_they_hold_and_the_energy_left_uses_the_measured_current()
    {
        var time = new ManualTime();
        await using var station = await StartAsync(time);
        var vehicle = await station.ConnectAsync();
        var monitor = station.Get<FlightMonitorWorker>();
        var notifications = station.Get<NotificationStore>();
        station.Get<SettingsStore>().Set(EnergySettings.CruiseCurrent, 10);

        await vehicle.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);
        station.Vehicle.SendLandedState(MavLandedState.InAir);
        station.Vehicle.SendHome(Home.Latitude, Home.Longitude, 488);
        var away = GeoMath.Offset(Home, 0, 3000);
        station.Vehicle.SendHud(18, 0, 100); // throttle travels with the next position
        station.Vehicle.SendPosition(away.Latitude, away.Longitude, 600, 112, northMps: 20);
        station.Vehicle.SendNavigation(800, 0, 40, -3, 5);
        station.Vehicle.SendBattery(15.8, 40, 30, consumedMah: 1000);
        await StationFixture.WaitForAsync(() => vehicle.Snapshot is { IsArmed: true, IsLanded: false, Navigation: not null, Home: not null, ThrottlePercent: 100, Battery.ConsumedMah: 1000 });

        monitor.Tick();
        Assert.Equal(3000, monitor.Status.HomeDistanceMeters!.Value, 0);
        Assert.Equal(150, monitor.Status.EndSeconds!.Value, 0); // home at the 20 m/s ground speed outside a mission
        Assert.Equal(CurrentSource.Entered, monitor.Status.Energy!.Source);
        Assert.DoesNotContain(notifications.Conditions, n => n.Title == "Far from the target altitude");

        // Held long enough: the altitude error and full throttle while slow become conditions.
        time.Offset = TimeSpan.FromSeconds(11);
        monitor.Tick();
        Assert.Contains(notifications.Conditions, n => n.Title == "Far from the target altitude");
        Assert.Contains(notifications.Conditions, n => n.Title == "Full throttle and too slow");

        // Two minutes later 3000 mAh more are used: 90 A on average, far beyond what is left above the reserve.
        time.Offset = TimeSpan.FromMinutes(2);
        station.Vehicle.SendBattery(15.0, 90, 2, consumedMah: 4000);
        await StationFixture.WaitForAsync(() => vehicle.Snapshot.Battery?.ConsumedMah == 4000);
        monitor.Tick();
        Assert.Equal(CurrentSource.Measured, monitor.Status.Energy!.Source);
        Assert.True(monitor.Status.Energy.MarginMah < 0);
        Assert.Contains(notifications.Conditions, n => n.Title == "Not enough battery to finish and return" && n.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public async Task The_plan_check_estimates_the_flight_and_warns_when_the_battery_does_not_last()
    {
        // No vehicle: the capacity comes from the settings and the reserve is PX4's default 15 %.
        await using var station = await StationFixture.StartAsync();
        station.Get<SettingsStore>().Set(EnergySettings.Capacity, 5000);
        var plans = station.Get<PlanStore>();
        var validation = station.Get<PlanValidationService>();
        plans.UpdateSettings(s => s with { PlannedHome = Home, HomeAltitudeMeters = 488, CruiseSpeedMps = 20 });
        plans.AddItem(MissionItemType.Takeoff, Home);
        plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 20_000));

        validation.Check();
        Assert.Equal(2000, validation.Estimate.Seconds, 0); // 20 km out and back at 20 m/s
        Assert.Null(validation.Budget);

        // 2000 s at 10 A is 5556 mAh; 5000 mAh × (1 − 0.15) = 4250 mAh is usable.
        station.Get<SettingsStore>().Set(EnergySettings.CruiseCurrent, 10);
        validation.Check();
        Assert.Equal(4250, validation.Budget!.UsableMah, 0);
        Assert.Contains(validation.Findings, f => f.Code == "mission.energy");
    }
}
