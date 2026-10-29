using Ketras.Comms;
using Ketras.Comms.MockLink;
using Ketras.Environment;
using Ketras.Mavlink;
using Ketras.Mavlink.Framing;
using Ketras.Mavlink.Messages;
using Ketras.Vehicles;

namespace Ketras.Tests.Mavlink;

public sealed class ConnectionTests
{
    [Fact]
    public async Task Discovers_the_vehicle_with_its_profile_mode_firmware_and_home()
    {
        await using var link = await TestLink.StartAsync(configure: vehicle => vehicle.Home = (47.397742, 8.545594, 488.2));
        var session = link.Session;

        Assert.Equal(1, session.SystemId);
        Assert.Equal("px4", session.Profile.Name);
        Assert.Equal(MavType.FixedWing, session.VehicleType);
        await TestLink.WaitForAsync(() => session.Status.State.Mode == "AUTO.LOITER" && session.Status.Firmware is not null && session.Status.Home is not null);
        Assert.Equal("1.15.0", session.Status.Firmware!.Version);
        Assert.Equal("1716151413121110", session.Status.Firmware.GitHash);
        Assert.Equal(488.2, session.Status.Home!.AltitudeAmsl, 3);
    }

    [Fact]
    public async Task Every_autopilot_on_the_link_gets_its_own_session()
    {
        var pair = InMemoryLink.Create();
        await using var connection = MavlinkConnection.Create(pair.Station);
        var discovered = new List<byte>();
        connection.VehicleDiscovered += session =>
        {
            lock (discovered)
            {
                discovered.Add(session.SystemId);
            }
        };
        await pair.Vehicle.OpenAsync();
        await connection.StartAsync();

        foreach (var system in new byte[] { 1, 2, 1 })
        {
            await pair.Vehicle.SendAsync(MavlinkFrame.Encode(new HeartbeatMessage { Type = (byte)MavType.FixedWing, Autopilot = (byte)MavAutopilot.Px4, MavlinkVersion = 3 }, 0, system, 1));
        }

        // A GCS and a component without an autopilot are not vehicles.
        await pair.Vehicle.SendAsync(MavlinkFrame.Encode(new HeartbeatMessage { Type = (byte)MavType.Gcs, Autopilot = (byte)MavAutopilot.Invalid }, 0, 200, 190));
        await pair.Vehicle.SendAsync(MavlinkFrame.Encode(new HeartbeatMessage { Type = (byte)MavType.Camera, Autopilot = (byte)MavAutopilot.Invalid }, 0, 1, 100));

        await TestLink.WaitForAsync(() => connection.Vehicles.Count == 2);
        await Task.Delay(200);
        Assert.Equal(new byte[] { 1, 2 }, connection.Vehicles.Select(v => v.SystemId));
        lock (discovered)
        {
            Assert.Equal(new byte[] { 1, 2 }, discovered.Order());
        }
    }

    [Fact]
    public async Task A_vehicle_whose_heartbeat_stops_is_lost_and_recovers_when_it_returns()
    {
        await using var link = await TestLink.StartAsync();
        var changes = new List<bool>();
        link.Session.ConnectionChanged += connected =>
        {
            lock (changes)
            {
                changes.Add(connected);
            }
        };

        link.Vehicle.HeartbeatPaused = true;
        await TestLink.WaitForAsync(() => !link.Session.IsConnected);
        link.Vehicle.HeartbeatPaused = false;
        await TestLink.WaitForAsync(() => link.Session.IsConnected);

        lock (changes)
        {
            Assert.Equal([false, true], changes);
        }
    }

    [Fact]
    public async Task A_transport_fault_marks_every_vehicle_lost_at_once()
    {
        await using var link = await TestLink.StartAsync(linkTimeout: TimeSpan.FromSeconds(30));
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Connection.Faulted += exception => faulted.TrySetResult(exception);

        link.Vehicle.StationEnd.Fault(new IOException("cable pulled"));

        Assert.Equal("cable pulled", (await faulted.Task.WaitAsync(TestLink.Timeout)).Message);
        Assert.False(link.Session.IsConnected);
        Assert.NotNull(link.Connection.Fault);
    }

    [Fact]
    public async Task A_failing_send_is_reported_and_counted_and_the_heartbeat_keeps_running()
    {
        await using var link = await TestLink.StartAsync();
        var failures = 0;
        link.Connection.SendFailed += _ => Interlocked.Increment(ref failures);

        link.Vehicle.StationEnd.FailSends = true;
        var result = await link.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);
        await TestLink.WaitForAsync(() => link.Connection.Statistics.SendErrors >= 2);

        Assert.Equal(Ketras.Mavlink.Protocols.CommandOutcome.NoResponse, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref failures));
        Assert.DoesNotContain(link.Vehicle.Commands, c => c.Command == MavCmd.ComponentArmDisarm);

        link.Vehicle.StationEnd.FailSends = false;
        var armed = await link.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);
        Assert.True(armed.IsAccepted);
        Assert.True(link.Session.IsConnected);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_other_subscribers_or_the_link()
    {
        await using var link = await TestLink.StartAsync();
        var seen = 0;
        link.Session.Status.PositionChanged += _ => throw new InvalidOperationException("bad subscriber");
        link.Session.Status.PositionChanged += _ => Interlocked.Increment(ref seen);

        link.Vehicle.SendPosition(47.39, 8.54, 500, 10);
        link.Vehicle.SendPosition(47.40, 8.55, 510, 20);
        await TestLink.WaitForAsync(() => Volatile.Read(ref seen) == 2);

        var accepted = await link.Session.Commands.SetModeAsync("AUTO.RTL");
        Assert.True(accepted.IsAccepted);
    }

    [Fact]
    public async Task Telemetry_reaches_the_status_traffic_wind_and_gps()
    {
        await using var link = await TestLink.StartAsync();
        var traffic = new TaskCompletionSource<AdsbTraffic>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Connection.TrafficReceived += report => traffic.TrySetResult(report);

        link.Vehicle.SendPosition(47.5, 8.5, 600, 110, headingDegrees: 90, northMps: 3, eastMps: 4);
        link.Vehicle.SendAttitude(10, -5, 270);
        link.Vehicle.SendWind(-5, 0, 0.5);
        link.Vehicle.SendGps(3, 14, 0.8);
        link.Vehicle.SendLandedState(MavLandedState.InAir);
        link.Vehicle.SendAdsb(0xABCDEF, "TEST123 ", 47.6, 8.6, 1500, 180, 60);

        var status = link.Session.Status;
        await TestLink.WaitForAsync(() => status.Position is not null && status.Attitude is not null && status.Wind is not null && status.Gps is not null && status.State.IsLanded == false);
        Assert.Equal(5, status.Position!.GroundSpeed, 3);
        Assert.Equal(53.13, status.Position.CourseDegrees!.Value, 2);
        Assert.Equal(270, status.Attitude!.YawDegrees, 3);
        Assert.Equal(0, status.Wind!.FromDegrees, 3);
        Assert.Equal(5, status.Wind.SpeedMps, 3);
        Assert.Equal(new GpsStatus(GpsFix.Fix3D, 14, 0.8, null), status.Gps);

        var report = await traffic.Task.WaitAsync(TestLink.Timeout);
        Assert.Equal(0xABCDEFu, report.IcaoAddress);
        Assert.Equal("TEST123", report.Callsign);
        Assert.Equal(1500, report.AltitudeMeters!.Value, 3);
        Assert.Equal(60, report.HorizontalSpeedMps!.Value, 3);
        Assert.Null(report.Squawk);
        Assert.Equal(1, report.SourceSystemId);
    }

    [Fact]
    public async Task Throttle_navigation_battery_estimator_and_vibration_reach_the_status()
    {
        await using var link = await TestLink.StartAsync();
        var status = link.Session.Status;

        link.Vehicle.SendHud(17, 1.2, 64);
        link.Vehicle.SendPosition(47.5, 8.5, 600, 110);
        link.Vehicle.SendNavigation(640, -90, 12.5, -2.5, -35);
        link.Vehicle.SendBattery(16.4, 12.3, 72, consumedMah: 1450, timeRemainingSeconds: 900);
        link.Vehicle.SendEstimator(0.2, 0.7);
        link.Vehicle.SendVibration(18, clipping: 3);

        await TestLink.WaitForAsync(() => status.Position?.ThrottlePercent is not null && status.Navigation is not null && status.BatteryState is not null
                                          && status.Estimator is not null && status.Vibration is not null);
        Assert.Equal(64, status.Position!.ThrottlePercent);
        Assert.Equal(new NavigationReport(640, 270, 12.5, -2.5, -35), status.Navigation);
        Assert.Equal(new BatteryReading(16.4, 12.3, 72, 1450, 900, null, 1), status.BatteryState);
        Assert.Equal(0.7, status.Estimator!.HorizontalPosition, 6);
        Assert.Equal(0.2, status.Estimator.Magnetometer, 6);
        Assert.Equal((18.0, 3L), (status.Vibration!.Z, status.Vibration.Clipping));
    }

    [Fact]
    public async Task The_tlog_records_both_directions_in_tlog_format()
    {
        await using var link = await TestLink.StartAsync();
        var stream = new MemoryStream();
        var recorder = new TlogWriter(stream, ownsStream: false);
        link.Connection.Recorder = recorder;

        var received = false;
        link.Session.Status.StatusText += (_, text) => received |= text == "recorded";
        await link.Session.Commands.SetModeAsync("AUTO.MISSION");
        link.Vehicle.SendStatusText(MavSeverity.Info, "recorded");
        // Heartbeats alone can pass a record count, so wait for the text itself: it is recorded before it is dispatched.
        await TestLink.WaitForAsync(() => received && recorder.Records > 5);
        link.Connection.Recorder = null;
        recorder.Dispose();

        stream.Position = 0;
        var records = TlogReader.Read(stream).ToList();
        var parser = new MavlinkParser();
        var packets = records.SelectMany(r => parser.Feed(r.Frame)).ToList();
        Assert.Equal(records.Count, packets.Count);
        Assert.Contains(packets, p => p.SystemId == 255 && p.Message is CommandLongMessage);
        Assert.Contains(packets, p => p.SystemId == 1 && p.Message is StatustextMessage { Text: "recorded" });
        Assert.All(records, r => Assert.InRange(r.TimeUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(1)));
    }
}
