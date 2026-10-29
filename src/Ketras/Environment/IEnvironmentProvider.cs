using Ketras.Model.Environment;
using Ketras.Model.Vehicles;

namespace Ketras.Environment;

/// <summary>
/// Strategy: a source of situational data around the vehicle (traffic, zones, points of interest, weather), e.g. an
/// ADS-B receiver, a file, an airspace service or a plugin's own server link. The station starts it with a sink to
/// report into and stops it on shutdown.
/// </summary>
public interface IEnvironmentProvider
{
    /// <summary>Provider name, used as the <c>Source</c> of what it reports.</summary>
    string Name { get; }

    /// <summary>Starts reporting into <paramref name="sink"/>; returns once started.</summary>
    Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation);

    /// <summary>Stops reporting.</summary>
    Task StopAsync(CancellationToken cancellation);
}

/// <summary>Implemented by a provider that reports the station's own vehicle outward (e.g. to a traffic service).</summary>
public interface IOwnshipConsumer
{
    /// <summary>The active vehicle's latest snapshot; called on a background thread at most 10 times a second.</summary>
    void OnOwnship(VehicleSnapshot ownship);
}

/// <summary>Where a provider reports. Every call is thread-safe; ids are unique across providers.</summary>
public interface IEnvironmentSink
{
    /// <summary>Adds or updates an aircraft.</summary>
    void ReportTraffic(TrafficTarget target);

    /// <summary>Removes an aircraft before it would time out.</summary>
    void RemoveTraffic(string id);

    /// <summary>Replaces every zone of <paramref name="source"/>.</summary>
    void SetZones(string source, IEnumerable<Zone> zones);

    /// <summary>Replaces every point of interest of <paramref name="source"/>.</summary>
    void SetPoints(string source, IEnumerable<PointOfInterest> points);

    /// <summary>Reports the latest weather from a source.</summary>
    void ReportWeather(WeatherSample sample);
}
