using Ketras.Mavlink.Messages;

namespace Ketras.Mavlink;

/// <summary>
/// One ADS-B report (ADSB_VEHICLE), e.g. from a transponder receiver on the autopilot. Fields the report marks as not
/// valid are null.
/// </summary>
/// <param name="IcaoAddress">ICAO 24-bit address.</param>
/// <param name="Callsign">Callsign, trimmed.</param>
/// <param name="Latitude">WGS84 degrees.</param>
/// <param name="Longitude">WGS84 degrees.</param>
/// <param name="AltitudeMeters">Altitude in metres, of the kind given by <paramref name="AltitudeType"/>.</param>
/// <param name="AltitudeType">Barometric (QNH) or geometric altitude.</param>
/// <param name="HeadingDegrees">Course over ground, 0…360.</param>
/// <param name="HorizontalSpeedMps">Ground speed.</param>
/// <param name="VerticalSpeedMps">Vertical speed, positive up.</param>
/// <param name="Squawk">Transponder code.</param>
/// <param name="EmitterType">Kind of aircraft.</param>
/// <param name="SecondsSinceLastContact">Age of the data when sent.</param>
/// <param name="IsSimulated">The report comes from a simulation.</param>
/// <param name="SourceSystemId">MAVLink system that forwarded the report.</param>
public sealed record AdsbTraffic(
    uint IcaoAddress,
    string? Callsign,
    double? Latitude,
    double? Longitude,
    double? AltitudeMeters,
    AdsbAltitudeType AltitudeType,
    double? HeadingDegrees,
    double? HorizontalSpeedMps,
    double? VerticalSpeedMps,
    ushort? Squawk,
    AdsbEmitterType EmitterType,
    int SecondsSinceLastContact,
    bool IsSimulated,
    byte SourceSystemId);
