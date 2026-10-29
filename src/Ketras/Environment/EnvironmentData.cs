using Ketras.Geo;

namespace Ketras.Environment;

/// <summary>What kind of aircraft a traffic target is (ADS-B emitter categories, simplified).</summary>
public enum TrafficCategory
{
    /// <summary>Not reported.</summary>
    Unknown,

    /// <summary>Light aircraft.</summary>
    Light,

    /// <summary>Small aircraft.</summary>
    Small,

    /// <summary>Large aircraft.</summary>
    Large,

    /// <summary>Heavy aircraft.</summary>
    Heavy,

    /// <summary>Helicopter or other rotorcraft.</summary>
    Rotorcraft,

    /// <summary>Glider or sailplane.</summary>
    Glider,

    /// <summary>Balloon or airship.</summary>
    LighterThanAir,

    /// <summary>Parachutist or skydiver.</summary>
    Parachute,

    /// <summary>Ultralight or paraglider.</summary>
    Ultralight,

    /// <summary>Unmanned aircraft.</summary>
    Unmanned,

    /// <summary>Something on the ground or a fixed obstacle.</summary>
    Surface
}

/// <summary>Which reference a traffic altitude is measured from.</summary>
public enum AltitudeReference
{
    /// <summary>Barometric altitude (QNH).</summary>
    Barometric,

    /// <summary>Geometric (GNSS) altitude.</summary>
    Geometric
}

/// <summary>
/// Another aircraft, as an environment provider reports it. Null fields are unknown. <see cref="SampleAge"/> is how old
/// the data already was when reported, so a screen can mark it stale without trusting clocks.
/// </summary>
public sealed record TrafficTarget
{
    /// <summary>Unique across providers, e.g. "adsb:4CA2B1".</summary>
    public required string Id { get; init; }

    /// <summary>Provider that reported it.</summary>
    public required string Source { get; init; }

    /// <summary>Callsign or name.</summary>
    public string? Callsign { get; init; }

    /// <summary>Position.</summary>
    public required GeoPoint Position { get; init; }

    /// <summary>Altitude in metres.</summary>
    public double? AltitudeMeters { get; init; }

    /// <summary>What <see cref="AltitudeMeters"/> is measured from.</summary>
    public AltitudeReference AltitudeReference { get; init; } = AltitudeReference.Geometric;

    /// <summary>Course over ground, 0…360 degrees.</summary>
    public double? HeadingDegrees { get; init; }

    /// <summary>Ground speed, m/s.</summary>
    public double? GroundSpeedMps { get; init; }

    /// <summary>Vertical speed, m/s, positive up.</summary>
    public double? VerticalSpeedMps { get; init; }

    /// <summary>Kind of aircraft.</summary>
    public TrafficCategory Category { get; init; }

    /// <summary>When the station received the report (UTC).</summary>
    public DateTime ReceivedUtc { get; init; }

    /// <summary>Age of the data when it was reported.</summary>
    public TimeSpan SampleAge { get; init; }
}

/// <summary>How a zone constrains flight.</summary>
public enum ZoneKind
{
    /// <summary>The vehicle must not enter.</summary>
    KeepOut,

    /// <summary>The vehicle must stay inside.</summary>
    StayIn,

    /// <summary>For information only.</summary>
    Advisory
}

/// <summary>
/// An area from an environment provider: a restriction, a boundary or an advisory, with an optional altitude band and
/// validity window. A keep-out or stay-in zone can be added to the vehicle's geofence when the plan is uploaded.
/// </summary>
public sealed record Zone
{
    /// <summary>Unique across providers.</summary>
    public required string Id { get; init; }

    /// <summary>Provider that reported it.</summary>
    public required string Source { get; init; }

    /// <summary>Name shown to the operator.</summary>
    public string? Name { get; init; }

    /// <summary>The area on the ground.</summary>
    public required Area Area { get; init; }

    /// <summary>How it constrains flight.</summary>
    public ZoneKind Kind { get; init; } = ZoneKind.KeepOut;

    /// <summary>Lower altitude limit above mean sea level; null from the ground.</summary>
    public double? FloorMeters { get; init; }

    /// <summary>Upper altitude limit above mean sea level; null without a limit.</summary>
    public double? CeilingMeters { get; init; }

    /// <summary>Start of validity (UTC); null when always valid.</summary>
    public DateTime? ValidFromUtc { get; init; }

    /// <summary>End of validity (UTC); null when open-ended.</summary>
    public DateTime? ValidUntilUtc { get; init; }

    /// <summary>Added to the geofence on upload.</summary>
    public bool IncludeInGeofence { get; init; }

    /// <summary>True when the zone applies at <paramref name="timeUtc"/>.</summary>
    public bool IsValidAt(DateTime timeUtc) =>
        (ValidFromUtc is null || timeUtc >= ValidFromUtc) && (ValidUntilUtc is null || timeUtc < ValidUntilUtc);
}

/// <summary>A labelled point from an environment provider, e.g. a landmark, a hazard or a point the operator should act on.</summary>
public sealed record PointOfInterest
{
    /// <summary>Unique across providers.</summary>
    public required string Id { get; init; }

    /// <summary>Provider that reported it.</summary>
    public required string Source { get; init; }

    /// <summary>Name shown to the operator.</summary>
    public string? Name { get; init; }

    /// <summary>Position.</summary>
    public required GeoPoint Location { get; init; }

    /// <summary>Altitude above mean sea level, if known.</summary>
    public double? AltitudeMeters { get; init; }

    /// <summary>Free-form category a provider uses, e.g. "landmark"; screens may style by it.</summary>
    public string? Category { get; init; }

    /// <summary>Longer description.</summary>
    public string? Description { get; init; }
}

/// <summary>Weather at a place, as an environment provider reports it. Null fields are unknown.</summary>
public sealed record WeatherSample
{
    /// <summary>Provider that reported it.</summary>
    public required string Source { get; init; }

    /// <summary>When it was measured (UTC).</summary>
    public DateTime TimeUtc { get; init; }

    /// <summary>Where it applies; null for the vehicle's own position.</summary>
    public GeoPoint? Location { get; init; }

    /// <summary>Direction the wind blows from, 0…360 degrees.</summary>
    public double? WindFromDegrees { get; init; }

    /// <summary>Horizontal wind speed, m/s.</summary>
    public double? WindSpeedMps { get; init; }

    /// <summary>Vertical wind, m/s, positive up.</summary>
    public double? VerticalWindMps { get; init; }

    /// <summary>Air temperature, °C.</summary>
    public double? TemperatureCelsius { get; init; }

    /// <summary>Relative humidity, 0…100.</summary>
    public double? HumidityPercent { get; init; }

    /// <summary>Static pressure, hPa.</summary>
    public double? PressureHectopascals { get; init; }
}
