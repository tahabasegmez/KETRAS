using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.Comms;
using Ketras.Geo;
using Ketras.Planning;
using Ketras.Planning.Missions;
using Ketras.Settings;
using Ketras.Setup;
using Ketras.Shell;
using Ketras.Telemetry;
using Ketras.Vehicles;

namespace Ketras.Tests.Telemetry;

[Collection(AvaloniaCollection.Name)]
public sealed class FlightTelemetryTests
{
    private static readonly GeoPoint Home = new(47.397971, 8.546164);

    private static async Task ConnectAsync(AppFixture app)
    {
        await app.StartAsync();
        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench" });
        await AppFixture.WaitForAsync(() => app.Get<VehicleRegistry>().Active is { Parameters.IsLoaded: true });
    }

    [Fact]
    public async Task The_expanded_panel_reads_navigation_power_and_the_flight_monitor()
    {
        await using var app = AppFixture.Create();
        await ConnectAsync(app);
        var panel = app.Get<TelemetryPanelViewModel>();
        var units = app.Get<Ketras.Utilities.UnitService>();
        Assert.False(panel.IsExpanded);

        panel.ToggleExpandedCommand.Execute(null);
        Assert.True(app.Get<SettingsStore>().Get(TelemetryPanelViewModel.ExpandedSetting));

        panel.Apply(new VehicleSnapshot
        {
            Id = new VehicleId(1), IsArmed = true, ThrottlePercent = 64,
            Navigation = new NavigationState(640, 90, 42, -1, 12),
            Battery = new BatteryState(16.4, 12.3, 1450, null, 4)
        });
        Assert.Equal((units.Distance(640), "+" + units.Altitude(42), units.Distance(12)), (panel.Waypoint, panel.AltitudeError, panel.CrossTrack));
        Assert.True(panel.IsAltitudeErrorWarning);
        Assert.False(panel.IsCrossTrackWarning);
        Assert.Equal(("64 %", "1450 mAh"), (panel.Throttle, panel.Consumed));
        Assert.Contains("4 cells", panel.VoltageDetail, StringComparison.Ordinal);

        panel.ApplyFlight(new FlightStatus(null, [], 32, 754, 3000, 182, new EnergyBudget(1000, 1100, 12, CurrentSource.Measured), TimeSpan.FromMinutes(5)));
        Assert.Equal(("0:32", "12:34", units.Distance(3000) + " 182°"), (panel.WaypointEta, panel.EndEta, panel.Home));
        Assert.Equal((ReadoutState.Error, true), (panel.MarginState, panel.IsMarginError));
        Assert.Contains("measured since arming", panel.MarginDetail, StringComparison.Ordinal);
        Assert.Equal("5:00", panel.TimeLeft); // 1000 mAh at 12 A

        Assert.Equal(("1:01:01", "0:05"), (TelemetryPanelViewModel.Duration(3661), TelemetryPanelViewModel.Duration(4.6)));
    }

    [Fact]
    public async Task The_status_bar_shows_gps_link_loss_and_the_flight_time_with_their_levels()
    {
        await using var app = AppFixture.Create();
        await ConnectAsync(app);
        app.Vehicle.SendGps(3, 5, 2.4);
        await AppFixture.WaitForAsync(() => app.Get<VehicleRegistry>().Active!.Snapshot.Satellites == 5);
        var bar = app.Get<FlightBarViewModel>();

        bar.Apply(new FlightStatus(new LinkQuality(12, null, null),
            [new FlightCheck("vehicle.gps", "GPS", CheckLevel.Warning, "3D fix, 5 satellites, HDOP 2.4"), new FlightCheck("vehicle.link", "Link", CheckLevel.Warning, "12 % lost")],
            null, null, null, null, null, TimeSpan.FromSeconds(75)));

        Assert.True(bar.HasVehicle);
        Assert.StartsWith("5 sat · HDOP 2", bar.Gps, StringComparison.Ordinal);
        Assert.Equal((true, "12 % loss", true, "1:15"), (bar.IsGpsWarning, bar.Link, bar.IsLinkWarning, bar.FlightTime));
        Assert.Equal("GPS: 3D fix, 5 satellites, HDOP 2.4", bar.GpsTip);
    }

    [Fact]
    public async Task Ready_to_fly_gathers_the_checks_the_mission_and_the_energy_and_the_power_page_sets_the_current()
    {
        await using var app = AppFixture.Create();
        await ConnectAsync(app);
        var setup = app.Get<SetupViewModel>();
        var readiness = setup.Summary.Readiness!;
        Assert.Equal(["arming", "vehicle.gps", "vehicle.estimator", "vehicle.vibration", "vehicle.battery", "vehicle.link", "mission", "energy", "wind"],
            readiness.Rows.Select(r => r.Code));

        var plans = app.Get<PlanStore>();
        plans.UpdateSettings(s => s with { PlannedHome = Home, CruiseSpeedMps = 20 });
        plans.AddItem(MissionItemType.Takeoff, Home);
        plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 6000));

        // Entered on the Power page: 10 A in cruise, 5000 mAh for a vehicle without BAT1_CAPACITY.
        setup.Power.CruiseCurrent.Text = "10";
        setup.Power.CruiseCurrent.CommitCommand.Execute(null);
        setup.Power.Capacity.Text = "5000";
        setup.Power.Capacity.CommitCommand.Execute(null);
        Assert.Equal(10, app.Get<SettingsStore>().Get(EnergySettings.CruiseCurrent));

        app.Get<PlanValidationService>().Check();
        readiness.Refresh();
        var row = readiness.Rows.ToDictionary(r => r.Code);
        Assert.Equal(CheckLevel.Warning, row["mission"].Level); // not uploaded
        Assert.Equal(CheckLevel.Pass, row["wind"].Level);
        // 600 s at 10 A = 1667 mAh of the 4250 mAh above the reserve.
        Assert.Equal(CheckLevel.Pass, row["energy"].Level);
        Assert.StartsWith("61 %", row["energy"].Detail, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_expanded_panel_the_estimate_line_and_the_ready_list_lay_out()
    {
        await using var app = AppFixture.Create();
        app.Get<SettingsStore>().Set(TelemetryPanelViewModel.ExpandedSetting, true);
        app.Get<SettingsStore>().Set(EnergySettings.CruiseCurrent, 10);
        app.Get<SettingsStore>().Set(EnergySettings.Capacity, 5000);
        var plans = app.Get<PlanStore>();
        plans.UpdateSettings(s => s with { PlannedHome = Home, CruiseSpeedMps = 20 });
        plans.AddItem(MissionItemType.Takeoff, Home);
        plans.AddItem(MissionItemType.Waypoint, GeoMath.Offset(Home, 0, 6000));
        app.Get<PlanValidationService>().Check();

        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var more = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "More");
        Assert.True(more.IsEffectivelyVisible);
        Assert.Equal(11, more.GetVisualDescendants().OfType<Grid>().Count(g => g.Classes.Contains("readout")));
        var estimate = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Estimate");
        Assert.StartsWith("≈ 10:00 · 12", estimate.Text, StringComparison.Ordinal);
        Assert.EndsWith("margin 61 %", estimate.Text, StringComparison.Ordinal);

        app.Get<ShellViewModel>().Show(ShellViewModel.Setup);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var ready = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "Ready");
        Assert.Equal(9, ready.ItemCount);
        Assert.True(ready.IsEffectivelyVisible);
        window.Close();
    }
}
