using Ketras.Station.Links;
using Ketras.Station.Vehicles;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Tests.Comms;

public sealed class LinkDiagnosticsTests
{
    private static LinkStep Step(LinkDiagnosticsWorkflow workflow, string title) => workflow.Steps.Single(s => s.Title == title);

    [Fact]
    public async Task A_connected_link_has_every_step_done_and_reports_its_details()
    {
        await using var station = await StationFixture.StartAsync();
        var workflow = station.Get<LinkDiagnosticsWorkflow>();

        await station.ConnectAsync();
        await StationFixture.WaitForAsync(() => workflow.Steps.Count == 4 && workflow.Steps.All(s => s.State == LinkStepState.Done) && workflow.Details?.MessagesPerSecond > 0);

        Assert.Equal(LinkPhase.Connected, workflow.Phase);
        Assert.Equal(["Open the link", "Receive data", "Vehicle heartbeat", "Parameters"], workflow.Steps.Select(s => s.Title));
        Assert.Equal("memory://station", Step(workflow, "Open the link").Detail); // the transport's own address
        Assert.Equal("system 1 · PX4 · Fixed wing", Step(workflow, "Vehicle heartbeat").Detail);
        Assert.Equal($"{StationFixture.Px4Parameters.Count} parameters", Step(workflow, "Parameters").Detail);
        var details = workflow.Details!;
        Assert.Equal((1, 1, "PX4", "Fixed wing"), (details.SystemId, details.ComponentId, details.Autopilot, details.VehicleType));
        Assert.True(details.SinceHeartbeat < TimeSpan.FromSeconds(2));
        Assert.Equal(0, details.LossRatio);

        await station.Get<ConnectionService>().DisconnectAsync();
        await StationFixture.WaitForAsync(() => workflow.Phase == LinkPhase.Idle);
        Assert.Empty(workflow.Steps);
        Assert.Null(workflow.Details);
    }

    [Fact]
    public async Task A_link_that_does_not_open_keeps_its_steps_as_the_last_attempt()
    {
        await using var station = await StationFixture.StartAsync(register: services => services.AddSingleton<ILinkFactory>(new FailingFactory()));
        var workflow = station.Get<LinkDiagnosticsWorkflow>();

        await station.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Radio", Kind = LinkKind.Serial, SerialPort = "COM9" });

        Assert.Equal(LinkPhase.LastAttempt, workflow.Phase);
        Assert.Equal(new LinkStep("Open the link", LinkStepState.Failed, "bad port"), Step(workflow, "Open the link"));
        Assert.Equal(LinkStepState.Pending, Step(workflow, "Receive data").State);
    }

    [Fact]
    public async Task Silence_after_opening_is_explained_and_a_lost_vehicle_fails_its_heartbeat_step()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: vehicle => vehicle.HeartbeatPaused = true);
        var workflow = new LinkDiagnosticsWorkflow(station.Get<ConnectionService>(), station.Get<VehicleRegistry>(), TimeProvider.System, TimeSpan.FromMilliseconds(300));
        await workflow.StartAsync(CancellationToken.None);

        await station.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Fake", Kind = LinkKind.Udp });
        await StationFixture.WaitForAsync(() => workflow.Steps.Any(s => s is { Title: "Receive data", State: LinkStepState.Warning }));
        Assert.Contains("on port 14550: is the vehicle sending to this computer?", Step(workflow, "Receive data").Detail, StringComparison.Ordinal);
        Assert.Equal(LinkStepState.Pending, Step(workflow, "Vehicle heartbeat").State);

        station.Vehicle.HeartbeatPaused = false;
        await StationFixture.WaitForAsync(() => workflow.Phase == LinkPhase.Connected);
        station.Vehicle.HeartbeatPaused = true;
        await StationFixture.WaitForAsync(() => workflow.Phase == LinkPhase.Lost);
        await StationFixture.WaitForAsync(() => Step(workflow, "Vehicle heartbeat").State == LinkStepState.Failed);
        Assert.StartsWith("last heard", Step(workflow, "Vehicle heartbeat").Detail, StringComparison.Ordinal);
        await workflow.StopAsync(CancellationToken.None);
    }

    private sealed class FailingFactory : ILinkFactory
    {
        public Mavlink.MavlinkConnection Create(LinkProfile profile, Mavlink.MavlinkConnectionOptions options) => throw new FormatException("bad port");
    }
}
