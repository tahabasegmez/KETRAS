using Ketras.Mavlink.Messages;
using Ketras.Station.Links;
using Ketras.Station.Notifications;
using Ketras.Station.Simulation;
using Ketras.Station.Vehicles;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Station.Tests;

public sealed class LinkNotificationsTests
{
    [Fact]
    public async Task Vehicle_warnings_and_errors_become_events_once_per_window_and_every_message_goes_to_the_link_log()
    {
        await using var station = await StationFixture.StartAsync();
        var store = station.Get<NotificationStore>();
        await station.ConnectAsync();

        station.Vehicle.SendStatusText(MavSeverity.Critical, "Preflight Fail: no GPS");
        station.Vehicle.SendStatusText(MavSeverity.Critical, "Preflight Fail: no GPS");
        station.Vehicle.SendStatusText(MavSeverity.Info, "Takeoff detected");
        station.Vehicle.SendStatusText(MavSeverity.Warning, "Low battery");
        await StationFixture.WaitForAsync(() => store.History.Any(n => n.Title == "Low battery"));

        var gps = Assert.Single(store.History, n => n.Title == "Preflight Fail: no GPS");
        Assert.Equal((NotificationSeverity.Error, NotificationKind.Event), (gps.Severity, gps.Kind));
        Assert.Equal(station.Get<VehicleRegistry>().Active!.Name, gps.Source);
        Assert.Equal(NotificationSeverity.Warning, store.History.Single(n => n.Title == "Low battery").Severity);
        Assert.DoesNotContain(store.History, n => n.Title == "Takeoff detected");

        // The link log keeps every message, the lesser ones and the repeats too.
        var log = station.Get<LinkLogStore>().Entries;
        Assert.Contains(log, e => e.Text.EndsWith(": Takeoff detected", StringComparison.Ordinal) && e.Severity == NotificationSeverity.Info);
        Assert.Equal(2, log.Count(e => e.Text.EndsWith(": Preflight Fail: no GPS", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_link_without_data_holds_a_condition_until_data_arrives()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: vehicle => vehicle.HeartbeatPaused = true);
        var store = station.Get<NotificationStore>();
        using var workflow = new LinkDiagnosticsWorkflow(station.Get<ConnectionService>(), station.Get<VehicleRegistry>(), TimeProvider.System, TimeSpan.FromMilliseconds(300));
        using var worker = new LinkNotificationsWorker(station.Get<ConnectionService>(), station.Get<VehicleRegistry>(), workflow, store, station.Get<LinkLogStore>(), TimeProvider.System);
        await workflow.StartAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);

        await station.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Fake", Kind = LinkKind.Udp });
        await StationFixture.WaitForAsync(() => store.Conditions.Any(c => c.Title == "No usable data on the link"));
        var condition = store.Conditions.Single(c => c.Title == "No usable data on the link");
        Assert.Equal(("Link", NotificationSeverity.Warning), (condition.Source, condition.Severity));
        Assert.Contains("is the vehicle sending to this computer?", condition.Detail, StringComparison.Ordinal);

        station.Vehicle.HeartbeatPaused = false;
        await StationFixture.WaitForAsync(() => store.Conditions.All(c => c.Title != "No usable data on the link"));
    }

    [Fact]
    public async Task A_simulator_coming_up_failing_to_start_or_stopping_on_its_own_is_reported()
    {
        var simulator = new LinkAndVehicleTests.FakeSimulator();
        await using var station = await StationFixture.StartAsync(register: services => services.AddSingleton<ISimulator>(simulator));
        var store = station.Get<NotificationStore>();
        var launch = new SimulatorLaunch("flat", default);

        await simulator.StartAsync(launch);
        simulator.Fail("no PX4 build");
        await simulator.StartAsync(launch);
        simulator.Run();
        simulator.Fail("PX4 exited (code 3)");

        Assert.Equal(
            [("fake stopped", NotificationSeverity.Error, "PX4 exited (code 3)"), ("fake is running", NotificationSeverity.Success, null), ("fake did not start", NotificationSeverity.Error, "no PX4 build")],
            store.History.Where(n => n.Source == "fake").Select(n => (n.Title, n.Severity, n.Detail)));
    }
}
