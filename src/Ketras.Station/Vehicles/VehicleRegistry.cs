using Ketras.Mavlink;
using Ketras.Model.Vehicles;
using Ketras.Station.Links;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Vehicles;

/// <summary>
/// Store of every vehicle the station follows and of the active one, which screens and commands act on. A vehicle is
/// added when its link discovers it (its parameters start loading then) and removed when the link closes. The first
/// vehicle becomes active. Events are raised on any thread.
/// </summary>
public sealed class VehicleRegistry : IDisposable
{
    private readonly TimeProvider _time;
    private readonly ILogger<VehicleRegistry> _logger;
    private readonly ConnectionService _connections;
    private readonly object _lock = new();
    private readonly List<(MavlinkConnection Connection, Vehicle Vehicle)> _vehicles = [];
    private Vehicle? _active;

    /// <summary>Follows the vehicles of every link <paramref name="connections"/> opens.</summary>
    public VehicleRegistry(ConnectionService connections, TimeProvider time, ILogger<VehicleRegistry> logger)
    {
        _connections = connections;
        _time = time;
        _logger = logger;
        connections.ConnectionOpened += OnOpened;
        connections.ConnectionClosed += OnClosed;
    }

    /// <summary>Every vehicle, by system id.</summary>
    public IReadOnlyList<Vehicle> Vehicles
    {
        get
        {
            lock (_lock)
            {
                return [.. _vehicles.Select(v => v.Vehicle).OrderBy(v => v.Id.SystemId)];
            }
        }
    }

    /// <summary>The vehicle screens and commands act on; null without vehicles.</summary>
    public Vehicle? Active
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>A vehicle was found.</summary>
    public event Action<Vehicle>? VehicleAdded;

    /// <summary>A vehicle is gone (its link closed).</summary>
    public event Action<Vehicle>? VehicleRemoved;

    /// <summary><see cref="Active"/> changed.</summary>
    public event Action<Vehicle?>? ActiveChanged;

    /// <summary>The vehicle with this id, if followed.</summary>
    public Vehicle? Find(VehicleId id)
    {
        lock (_lock)
        {
            return _vehicles.Select(v => v.Vehicle).FirstOrDefault(v => v.Id == id);
        }
    }

    /// <summary>Makes a followed vehicle the active one.</summary>
    public bool SetActive(VehicleId id)
    {
        Vehicle? vehicle;
        lock (_lock)
        {
            vehicle = _vehicles.Select(v => v.Vehicle).FirstOrDefault(v => v.Id == id);
            if (vehicle is null || ReferenceEquals(vehicle, _active))
            {
                return vehicle is not null;
            }

            _active = vehicle;
        }

        ActiveChanged?.Invoke(vehicle);
        return true;
    }

    private void OnOpened(MavlinkConnection connection) =>
        connection.VehicleDiscovered += session =>
        {
            var vehicle = new Vehicle(session, $"Vehicle {session.SystemId}", _time, _logger);
            bool becameActive;
            lock (_lock)
            {
                _vehicles.Add((connection, vehicle));
                becameActive = _active is null;
                if (becameActive)
                {
                    _active = vehicle;
                }
            }

            VehicleAdded?.Invoke(vehicle);
            if (becameActive)
            {
                ActiveChanged?.Invoke(vehicle);
            }

            _ = vehicle.Parameters.RefreshAsync();
        };

    private void OnClosed(MavlinkConnection connection)
    {
        List<Vehicle> removed;
        bool activeChanged;
        Vehicle? active;
        lock (_lock)
        {
            removed = [.. _vehicles.Where(v => ReferenceEquals(v.Connection, connection)).Select(v => v.Vehicle)];
            _vehicles.RemoveAll(v => ReferenceEquals(v.Connection, connection));
            activeChanged = _active is not null && removed.Contains(_active);
            if (activeChanged)
            {
                _active = _vehicles.Select(v => v.Vehicle).OrderBy(v => v.Id.SystemId).FirstOrDefault();
            }

            active = _active;
        }

        foreach (var vehicle in removed)
        {
            vehicle.Dispose();
            VehicleRemoved?.Invoke(vehicle);
        }

        if (activeChanged)
        {
            ActiveChanged?.Invoke(active);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connections.ConnectionOpened -= OnOpened;
        _connections.ConnectionClosed -= OnClosed;
        lock (_lock)
        {
            foreach (var (_, vehicle) in _vehicles)
            {
                vehicle.Dispose();
            }

            _vehicles.Clear();
        }
    }
}
