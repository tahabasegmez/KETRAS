
namespace Ketras.Environment;

/// <summary>
/// Zones and points of interest the operator draws or types in, kept apart from the mission's own geofence. Thread-safe.
/// </summary>
public sealed class ManualEnvironmentProvider : IEnvironmentProvider
{
    private readonly object _lock = new();
    private readonly List<Zone> _zones = [];
    private readonly List<PointOfInterest> _points = [];
    private IEnvironmentSink? _sink;

    /// <inheritdoc />
    public string Name => "Manual";

    /// <summary>The operator's zones.</summary>
    public IReadOnlyList<Zone> Zones
    {
        get
        {
            lock (_lock)
            {
                return [.. _zones];
            }
        }
    }

    /// <summary>The operator's points.</summary>
    public IReadOnlyList<PointOfInterest> Points
    {
        get
        {
            lock (_lock)
            {
                return [.. _points];
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation)
    {
        _sink = sink;
        Publish();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellation)
    {
        _sink = null;
        return Task.CompletedTask;
    }

    /// <summary>Adds a zone or replaces the one with its id; its source becomes this provider.</summary>
    public void UpsertZone(Zone zone)
    {
        lock (_lock)
        {
            _zones.RemoveAll(z => z.Id == zone.Id);
            _zones.Add(zone with { Source = Name });
        }

        Publish();
    }

    /// <summary>Removes a zone.</summary>
    public void RemoveZone(string id)
    {
        lock (_lock)
        {
            _zones.RemoveAll(z => z.Id == id);
        }

        Publish();
    }

    /// <summary>Adds a point or replaces the one with its id; its source becomes this provider.</summary>
    public void UpsertPoint(PointOfInterest point)
    {
        lock (_lock)
        {
            _points.RemoveAll(p => p.Id == point.Id);
            _points.Add(point with { Source = Name });
        }

        Publish();
    }

    /// <summary>Removes a point.</summary>
    public void RemovePoint(string id)
    {
        lock (_lock)
        {
            _points.RemoveAll(p => p.Id == id);
        }

        Publish();
    }

    private void Publish()
    {
        if (_sink is not { } sink)
        {
            return;
        }

        sink.SetZones(Name, Zones);
        sink.SetPoints(Name, Points);
    }
}
