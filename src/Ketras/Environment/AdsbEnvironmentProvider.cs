using System.Globalization;
using Ketras.Comms;
using Ketras.Geo;
using Ketras.Mavlink;
using Ketras.Mavlink.Messages;

namespace Ketras.Environment;

/// <summary>
/// Traffic from ADSB_VEHICLE messages on the vehicle link, as an autopilot with an ADS-B receiver forwards them. A
/// report without a valid position is ignored; its other fields keep their validity flags (null when not valid).
/// </summary>
public sealed class AdsbEnvironmentProvider(ConnectionService connections) : IEnvironmentProvider
{
    private IEnvironmentSink? _sink;

    /// <inheritdoc />
    public string Name => "ADS-B";

    /// <inheritdoc />
    public Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation)
    {
        _sink = sink;
        connections.ConnectionOpened += Attach;
        if (connections.Connection is { } open)
        {
            Attach(open);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellation)
    {
        connections.ConnectionOpened -= Attach;
        _sink = null;
        return Task.CompletedTask;
    }

    private void Attach(MavlinkConnection connection) => connection.TrafficReceived += Report;

    private void Report(AdsbTraffic report)
    {
        if (_sink is not { } sink || report.Latitude is not { } latitude || report.Longitude is not { } longitude)
        {
            return;
        }

        sink.ReportTraffic(new TrafficTarget
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"adsb:{report.IcaoAddress:X6}"),
            Source = Name,
            Callsign = report.Callsign,
            Position = new GeoPoint(latitude, longitude),
            AltitudeMeters = report.AltitudeMeters,
            AltitudeReference = report.AltitudeType == AdsbAltitudeType.PressureQnh ? AltitudeReference.Barometric : AltitudeReference.Geometric,
            HeadingDegrees = report.HeadingDegrees,
            GroundSpeedMps = report.HorizontalSpeedMps,
            VerticalSpeedMps = report.VerticalSpeedMps,
            Category = CategoryOf(report.EmitterType),
            SampleAge = TimeSpan.FromSeconds(report.SecondsSinceLastContact)
        });
    }

    /// <summary>ADSB_EMITTER_TYPE (common.xml) to the station's categories.</summary>
    private static TrafficCategory CategoryOf(AdsbEmitterType type) => type switch
    {
        AdsbEmitterType.Light => TrafficCategory.Light,
        AdsbEmitterType.Small => TrafficCategory.Small,
        AdsbEmitterType.Large or AdsbEmitterType.HighVortexLarge => TrafficCategory.Large,
        AdsbEmitterType.Heavy or AdsbEmitterType.HighlyManuv => TrafficCategory.Heavy,
        AdsbEmitterType.Rotocraft => TrafficCategory.Rotorcraft,
        AdsbEmitterType.Glider => TrafficCategory.Glider,
        AdsbEmitterType.LighterAir => TrafficCategory.LighterThanAir,
        AdsbEmitterType.Parachute => TrafficCategory.Parachute,
        AdsbEmitterType.UltraLight => TrafficCategory.Ultralight,
        AdsbEmitterType.Uav => TrafficCategory.Unmanned,
        AdsbEmitterType.EmergencySurface or AdsbEmitterType.ServiceSurface or AdsbEmitterType.PointObstacle => TrafficCategory.Surface,
        _ => TrafficCategory.Unknown
    };
}
