using Ketras.Geo;

namespace Ketras.Planning.Missions;

/// <summary>Typed reads of the raw MAVLink parameters, so callers need not know each command's parameter layout.</summary>
public static class MissionItemValues
{
    /// <summary>
    /// Where a landing pattern's straight final begins: <c>Param3</c> metres back from touchdown along the reciprocal
    /// of the approach heading (<c>Param4</c>). The orbit is left on its tangent here.
    /// </summary>
    public static GeoPoint? LandingPatternApproachStart(MissionItem item) =>
        item.Type == MissionItemType.LandingPattern && item.Location is { } touchdown
            ? GeoMath.Offset(touchdown, (item.Param4 + 180) % 360, item.Param3)
            : null;

    /// <summary>
    /// Orbit centre of a landing pattern: one radius sideways from the approach start, to the right of the approach
    /// for a clockwise orbit and to the left otherwise.
    /// </summary>
    public static GeoPoint? LandingPatternOrbitCenter(MissionItem item) =>
        LandingPatternApproachStart(item) is { } start
            ? GeoMath.Offset(start, (item.Param4 + (IsClockwise(item) ? 90 : 270)) % 360, Math.Abs(item.Param2))
            : null;

    /// <summary>Glide slope of a landing pattern in degrees, from approach altitude down to touchdown altitude.</summary>
    public static double? LandingGlideSlopeDegrees(MissionItem item) =>
        item.Type == MissionItemType.LandingPattern && item.Param3 > 0
            ? Math.Atan2(item.AltitudeMeters - item.Param1, item.Param3) * 180d / Math.PI
            : null;

    /// <summary>Orbit direction; a negative radius means counter-clockwise, as in MAVLink.</summary>
    public static bool IsClockwise(MissionItem item) => (LoiterRadiusParam(item) ?? 0) >= 0;

    /// <summary>Orbit radius in metres, for the item types that orbit.</summary>
    public static double? LoiterRadiusMeters(MissionItem item) =>
        LoiterRadiusParam(item) is { } radius ? Math.Abs(radius) : null;

    /// <summary>Target airspeed of a speed change.</summary>
    public static double? SpeedMps(MissionItem item) =>
        item.Type == MissionItemType.ChangeSpeed ? item.Param2 : null;

    /// <summary>Approach heading of a landing pattern.</summary>
    public static double? HeadingDegrees(MissionItem item) =>
        item.Type == MissionItemType.LandingPattern ? item.Param4 : null;

    /// <summary>Touchdown altitude above home of a landing item.</summary>
    public static double? TouchdownAltitudeMeters(MissionItem item) => item.Type switch
    {
        MissionItemType.LandingPattern => item.Param1,
        MissionItemType.Land => item.AltitudeMeters,
        _ => null
    };

    private static double? LoiterRadiusParam(MissionItem item) => item.Type switch
    {
        MissionItemType.LoiterUnlimited or MissionItemType.LoiterTime => item.Param3,
        MissionItemType.LoiterToAltitude or MissionItemType.LandingPattern => item.Param2,
        _ => null
    };
}
