using Ketras.Comms;
using Ketras.Notifications;
using Ketras.Planning;
using Ketras.Simulation;
using Ketras.Vehicles;
using Microsoft.Extensions.DependencyInjection;

namespace Ketras.Tests.Comms;

public sealed class LinkAndVehicleTests
{
    [Fact]
    public async Task Connecting_follows_the_vehicle_loads_its_parameters_and_records_a_tlog()
    {
        await using var station = await StationFixture.StartAsync();
        var states = new List<LinkState>();
        station.Get<ConnectionService>().StatusChanged += status =>
        {
            lock (states)
            {
                states.Add(status.State);
            }
        };

        var vehicle = await station.ConnectAsync();
        await station.Get<ConnectionService>().DisconnectAsync();

        Assert.Equal(new VehicleId(1), vehicle.Id);
        Assert.Equal(25, vehicle.Parameters["FW_AIRSPD_MAX"]);
        lock (states)
        {
            Assert.Equal([LinkState.Connecting, LinkState.Connected, LinkState.Disconnected], states.Distinct());
        }

        Assert.Empty(station.Get<VehicleRegistry>().Vehicles);
        Assert.Null(station.Get<VehicleRegistry>().Active);
        var tlog = Assert.Single(Directory.GetFiles(Path.Combine(station.Directory, "data", "tlogs"), "*.tlog"));
        await using var file = File.OpenRead(tlog);
        Assert.True(TlogReader.Read(file).Count() > 10);
        var titles = station.Get<NotificationStore>().History.Select(n => n.Title).ToList();
        Assert.Contains("Vehicle connected", titles);
        Assert.Contains("Disconnected", titles);
    }

    [Fact]
    public async Task A_silent_vehicle_makes_the_link_lost_with_a_condition_until_it_returns()
    {
        await using var station = await StationFixture.StartAsync();
        await station.ConnectAsync();
        var connection = station.Get<ConnectionService>();
        var notifications = station.Get<NotificationStore>();

        station.Vehicle.HeartbeatPaused = true;
        await StationFixture.WaitForAsync(() => connection.Status.State == LinkState.Lost);
        Assert.Contains(notifications.Conditions, n => n.Source == "Link" && n.Key == "lost");

        station.Vehicle.HeartbeatPaused = false;
        await StationFixture.WaitForAsync(() => connection.Status.State == LinkState.Connected);
        Assert.DoesNotContain(notifications.Conditions, n => n.Source == "Link");
        Assert.Contains(notifications.History, n => n.Title == "Vehicle link restored");
    }

    [Fact]
    public async Task Snapshots_carry_position_state_and_home_and_limits_come_from_px4_parameters()
    {
        await using var station = await StationFixture.StartAsync();
        var vehicle = await station.ConnectAsync();

        station.Vehicle.SendPosition(47.39, 8.54, 520, 30, headingDegrees: 45, northMps: 15, eastMps: 0);
        station.Vehicle.SendHome(47.38, 8.53, 490);
        station.Vehicle.SendGps(3, 12, 0.9);
        await StationFixture.WaitForAsync(() => vehicle.Snapshot is { Position: not null, Home: not null, Fix: PositionFix.Fix3D });

        var snapshot = vehicle.Snapshot;
        Assert.Equal(30, snapshot.RelativeAltitudeMeters);
        Assert.Equal("AUTO.LOITER", snapshot.Mode);
        Assert.Equal(12, snapshot.Satellites);
        Assert.Equal(new VehicleLimits
        {
            MinAirspeedMps = 12, MaxAirspeedMps = 25, MaxBankDegrees = 40, MaxLandingSlopeDegrees = 6,
            DefaultLoiterRadiusMeters = 90, ReturnAltitudeMeters = 60
        }, vehicle.Limits);

        await StationFixture.WaitForAsync(() => station.Get<PlanStore>().Current.Mission.Settings.PlannedHome.Latitude == 47.38);
        Assert.Equal(490, station.Get<PlanStore>().Current.Mission.Settings.HomeAltitudeMeters);
    }

    [Fact]
    public async Task A_link_that_cannot_open_is_reported_not_thrown()
    {
        await using var station = await StationFixture.StartAsync(register: services => services.AddSingleton<ILinkFactory>(new ThrowingFactory()));

        var opened = await station.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bad", Kind = LinkKind.Serial, SerialPort = "COM99" });

        Assert.False(opened);
        Assert.Equal(LinkState.Disconnected, station.Get<ConnectionService>().Status.State);
        Assert.Contains("format", station.Get<ConnectionService>().Status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(station.Get<NotificationStore>().History, n => n.Title == "Not connected");
    }

    [Fact]
    public async Task A_simulator_link_starts_its_simulator_and_connects_once_it_runs()
    {
        var simulator = new FakeSimulator();
        await using var station = await StationFixture.StartAsync(register: services => services.AddSingleton<ISimulator>(simulator));

        var connecting = station.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "SITL", Kind = LinkKind.Simulator, SimulatorId = "fake" });
        await StationFixture.WaitForAsync(() => simulator.Started);
        Assert.Contains("fake: building", station.Get<ConnectionService>().Status.Detail);
        simulator.Run();

        Assert.True(await connecting);
        Assert.Equal("SITL", station.Factory.Opened?.Name);
        Assert.Equal("udp://:18570", station.Factory.Opened?.Address);
    }

    [Fact]
    public async Task A_simulator_that_stops_before_it_is_ready_fails_the_attempt()
    {
        var simulator = new FakeSimulator();
        await using var station = await StationFixture.StartAsync(register: services => services.AddSingleton<ISimulator>(simulator));

        var connecting = station.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "SITL", Kind = LinkKind.Simulator, SimulatorId = "fake" });
        await StationFixture.WaitForAsync(() => simulator.Started);
        simulator.Fail("no PX4 build");

        Assert.False(await connecting);
        Assert.Equal("no PX4 build", station.Get<ConnectionService>().Status.Error);
    }

    [Fact]
    public async Task Saved_links_survive_a_restart_and_unknown_kinds_are_dropped()
    {
        await using var station = await StationFixture.StartAsync();
        var store = station.Get<LinkProfileStore>();
        store.Save(new LinkProfile { Name = "Radio", Kind = LinkKind.Serial, SerialPort = "/dev/ttyUSB0", BaudRate = 57600 });
        store.Save(new LinkProfile { Name = "Radio 2", Kind = LinkKind.Tcp, Host = "10.0.0.2", Port = 5760 }, replaces: "Radio");
        var path = Path.Combine(station.Directory, "config", "links.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"tcp\"", "\"carrierPigeon\"", StringComparison.Ordinal));

        var reopened = new LinkProfileStore(station.Get<Ketras.Settings.StationPaths>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LinkProfileStore>.Instance);

        Assert.Empty(reopened.Profiles);
        Assert.Equal("serial:/dev/ttyUSB0:57600", new LinkProfile { Name = "x", Kind = LinkKind.Serial, SerialPort = "/dev/ttyUSB0" }.Address);
        Assert.Equal("udp://:14550", new LinkProfile { Name = "x" }.Address);
        Assert.Equal("udp://10.0.0.5:18570", new LinkProfile { Name = "x", Host = "10.0.0.5", Port = 18570 }.Address);
    }

    private sealed class ThrowingFactory : ILinkFactory
    {
        public Ketras.Mavlink.MavlinkConnection Create(LinkProfile profile, Ketras.Mavlink.MavlinkConnectionOptions options) =>
            throw new FormatException("bad serial format");
    }

    internal sealed class FakeSimulator : ISimulator
    {
        private SimulatorStatus _status = SimulatorStatus.Stopped;

        public bool Started { get; private set; }

        public SimulatorDescription Description { get; } = new()
        {
            Id = "fake", Name = "fake",
            Worlds = [new SimulatorWorld("flat", "Flat", "", false) { HasTerrain = false }, new SimulatorWorld("terrain", "Terrain", "", true)]
        };

        public SimulatorStatus Status => _status;

        public LinkProfile? Link { get; private set; }

        public event Action<SimulatorStatus>? StatusChanged;

        public event Action<string, SimulatorOutputLevel>? Output;

        public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;

        public Task StartAsync(SimulatorLaunch launch, CancellationToken cancellation = default)
        {
            Started = true;
            Set(new SimulatorStatus(SimulatorRunState.Starting, "building"));
            Output?.Invoke("starting", SimulatorOutputLevel.Info);
            return Task.CompletedTask;
        }

        public void Run(string? world = null)
        {
            Link = new LinkProfile { Name = "fake", Port = 18570 };
            Set(new SimulatorStatus(SimulatorRunState.Running) { WorldId = world });
        }

        public void Fail(string error) => Set(new SimulatorStatus(SimulatorRunState.Stopped, Error: error));

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Set(SimulatorStatus status)
        {
            _status = status;
            StatusChanged?.Invoke(status);
        }
    }
}
