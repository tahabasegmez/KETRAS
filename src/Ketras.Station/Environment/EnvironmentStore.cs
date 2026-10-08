using Ketras.Model.Environment;
using Ketras.Model.Fences;
using Ketras.Station.Settings;
using Microsoft.Extensions.Options;

namespace Ketras.Station.Environment;

/// <summary>What part of the environment changed.</summary>
[Flags]
public enum EnvironmentChange
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Traffic.</summary>
    Traffic = 1,

    /// <summary>Zones.</summary>
    Zones = 2,

    /// <summary>Points of interest.</summary>
    Points = 4,

    /// <summary>Weather.</summary>
    Weather = 8
}

/// <summary>
/// Store of everything the environment providers report, merged across providers. Traffic not heard from for the
/// configured timeout is dropped. Thread-safe; <see cref="Changed"/> is raised on the reporting thread.
/// </summary>
public sealed class EnvironmentStore : IEnvironmentSink, IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _trafficTimeout;
    private readonly ITimer _expiry;
    private readonly object _lock = new();
    private readonly Dictionary<string, TrafficTarget> _traffic = [];
    private readonly Dictionary<string, IReadOnlyList<Zone>> _zones = [];
    private readonly Dictionary<string, IReadOnlyList<PointOfInterest>> _points = [];
    private readonly Dictionary<string, WeatherSample> _weather = [];

    /// <summary>Creates the store; traffic expiry follows <see cref="EnvironmentOptions.TrafficTimeout"/>.</summary>
    public EnvironmentStore(TimeProvider time, IOptions<StationOptions> options)
    {
        _time = time;
        _trafficTimeout = options.Value.Environment.TrafficTimeout;
        _expiry = time.CreateTimer(_ => Expire(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Something changed.</summary>
    public event Action<EnvironmentChange>? Changed;

    /// <summary>Every aircraft heard recently.</summary>
    public IReadOnlyList<TrafficTarget> Traffic
    {
        get
        {
            lock (_lock)
            {
                return [.. _traffic.Values];
            }
        }
    }

    /// <summary>Every zone of every provider.</summary>
    public IReadOnlyList<Zone> Zones
    {
        get
        {
            lock (_lock)
            {
                return [.. _zones.Values.SelectMany(z => z)];
            }
        }
    }

    /// <summary>Every point of interest of every provider.</summary>
    public IReadOnlyList<PointOfInterest> Points
    {
        get
        {
            lock (_lock)
            {
                return [.. _points.Values.SelectMany(p => p)];
            }
        }
    }

    /// <summary>The latest weather of each source.</summary>
    public IReadOnlyList<WeatherSample> Weather
    {
        get
        {
            lock (_lock)
            {
                return [.. _weather.Values];
            }
        }
    }

    /// <summary>
    /// The keep-out and stay-in zones marked for the geofence and valid now, as fences: what an upload adds to the
    /// operator's geofence.
    /// </summary>
    public IReadOnlyList<Fence> GeofenceZones()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return [.. Zones.Where(z => z.IncludeInGeofence && z.Kind != ZoneKind.Advisory && z.IsValidAt(now))
            .Select(z => new Fence($"{z.Source}:{z.Id}", z.Area, z.Kind == ZoneKind.StayIn))];
    }

    /// <inheritdoc />
    public void ReportTraffic(TrafficTarget target)
    {
        lock (_lock)
        {
            _traffic[target.Id] = target with { ReceivedUtc = _time.GetUtcNow().UtcDateTime };
        }

        Changed?.Invoke(EnvironmentChange.Traffic);
    }

    /// <inheritdoc />
    public void RemoveTraffic(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _traffic.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke(EnvironmentChange.Traffic);
        }
    }

    /// <inheritdoc />
    public void SetZones(string source, IEnumerable<Zone> zones)
    {
        lock (_lock)
        {
            _zones[source] = [.. zones];
        }

        Changed?.Invoke(EnvironmentChange.Zones);
    }

    /// <inheritdoc />
    public void SetPoints(string source, IEnumerable<PointOfInterest> points)
    {
        lock (_lock)
        {
            _points[source] = [.. points];
        }

        Changed?.Invoke(EnvironmentChange.Points);
    }

    /// <inheritdoc />
    public void ReportWeather(WeatherSample sample)
    {
        lock (_lock)
        {
            _weather[sample.Source] = sample;
        }

        Changed?.Invoke(EnvironmentChange.Weather);
    }

    private void Expire()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _trafficTimeout;
        int removed;
        lock (_lock)
        {
            var stale = _traffic.Values.Where(t => t.ReceivedUtc - t.SampleAge < cutoff).Select(t => t.Id).ToList();
            stale.ForEach(id => _traffic.Remove(id));
            removed = stale.Count;
        }

        if (removed > 0)
        {
            Changed?.Invoke(EnvironmentChange.Traffic);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _expiry.Dispose();
}
