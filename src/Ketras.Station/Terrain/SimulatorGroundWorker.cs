using Ketras.Station.Links;
using Ketras.Station.Notifications;
using Ketras.Station.Simulation;
using Microsoft.Extensions.Hosting;

namespace Ketras.Station.Terrain;

/// <summary>
/// Worker that keeps the terrain honest about a simulator: while a simulator runs a world without terrain
/// (<see cref="SimulatorWorld.HasTerrain"/> false, e.g. PX4's flat world) and the station is not linked to anything else,
/// <see cref="TerrainService"/> takes the ground as level with the planned home. Without this a touchdown would be
/// planned at the real terrain's height, and the vehicle would flare in the air or fly into the simulated ground; the
/// terrain checks would also warn about hills the simulator does not have. The switch is said once as a notification.
/// </summary>
public sealed class SimulatorGroundWorker : IHostedService, IDisposable
{
    private const string Source = "Terrain";

    private readonly ConnectionService _connections;
    private readonly TerrainService _terrain;
    private readonly NotificationStore _notifications;
    private readonly object _lock = new();
    private bool? _flat;

    /// <summary>Creates the worker.</summary>
    public SimulatorGroundWorker(ConnectionService connections, TerrainService terrain, NotificationStore notifications)
    {
        _connections = connections;
        _terrain = terrain;
        _notifications = notifications;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _connections.StatusChanged += OnLink;
        foreach (var simulator in _connections.Simulators)
        {
            simulator.StatusChanged += OnSimulator;
        }

        Update();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connections.StatusChanged -= OnLink;
        foreach (var simulator in _connections.Simulators)
        {
            simulator.StatusChanged -= OnSimulator;
        }
    }

    /// <summary>True when a simulator runs a world without terrain and the link, if any, goes to that simulator.</summary>
    public static bool IsFlat(LinkStatus link, IEnumerable<ISimulator> simulators) => simulators.Any(simulator =>
        simulator.Status is { State: SimulatorRunState.Starting or SimulatorRunState.Running, WorldId: { } world }
        && simulator.Description.Worlds.FirstOrDefault(w => w.Id == world) is { HasTerrain: false }
        && (link.State == LinkState.Disconnected || link.Profile is { Kind: LinkKind.Simulator } profile && profile.SimulatorId == simulator.Description.Id));

    private void OnLink(LinkStatus status) => Update();

    private void OnSimulator(SimulatorStatus status) => Update();

    private void Update()
    {
        var flat = IsFlat(_connections.Status, _connections.Simulators);
        lock (_lock)
        {
            if (_flat == flat)
            {
                return;
            }

            var first = _flat is null;
            _flat = flat;
            if (first && !flat)
            {
                return;
            }
        }

        _terrain.UseFlatGround(flat);
        _notifications.Notify(Source, NotificationSeverity.Info, flat
            ? "The simulator's world is flat: the plan uses level ground at the home"
            : "The plan uses the real terrain again");
    }
}
