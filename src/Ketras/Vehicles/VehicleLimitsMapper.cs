
namespace Ketras.Vehicles;

/// <summary>
/// PX4 fixed-wing parameters to the planning envelope (<see cref="VehicleLimits"/>). Stall speed scales with the
/// square root of the weight ratio. Another autopilot keeps the defaults until it has a mapping.
/// </summary>
public static class VehicleLimitsMapper
{
    /// <summary>The PX4 parameters the limits come from.</summary>
    public static IReadOnlyList<string> Px4Parameters { get; } =
    [
        "FW_AIRSPD_MIN", "FW_AIRSPD_MAX", "FW_R_LIM", "FW_LND_ANG", "NAV_LOITER_RAD", "WEIGHT_BASE", "WEIGHT_GROSS",
        "FW_T_CLMB_MAX", "FW_T_SINK_MAX", "RTL_RETURN_ALT"
    ];

    /// <summary>Limits from PX4 parameter values; null until the airspeed limits are known.</summary>
    public static VehicleLimits? FromPx4(Func<string, double?> parameter)
    {
        if (parameter("FW_AIRSPD_MIN") is not { } min || parameter("FW_AIRSPD_MAX") is not { } max)
        {
            return null;
        }

        var ratio = parameter("WEIGHT_BASE") is { } weightBase and > 0 && parameter("WEIGHT_GROSS") is { } gross and > 0
            ? Math.Clamp(gross / weightBase, 0.5, 2)
            : 1;
        var defaults = VehicleLimits.Default;
        return new VehicleLimits
        {
            MinAirspeedMps = Math.Round(min * Math.Sqrt(ratio), 2),
            MaxAirspeedMps = max,
            MaxBankDegrees = parameter("FW_R_LIM") ?? defaults.MaxBankDegrees,
            MaxLandingSlopeDegrees = parameter("FW_LND_ANG") ?? defaults.MaxLandingSlopeDegrees,
            DefaultLoiterRadiusMeters = parameter("NAV_LOITER_RAD") is { } radius && Math.Abs(radius) > 0 ? Math.Abs(radius) : defaults.DefaultLoiterRadiusMeters,
            MaxClimbRateMps = parameter("FW_T_CLMB_MAX") is { } climb and > 0 ? climb : defaults.MaxClimbRateMps,
            MaxSinkRateMps = parameter("FW_T_SINK_MAX") is { } sink and > 0 ? sink : defaults.MaxSinkRateMps,
            ReturnAltitudeMeters = parameter("RTL_RETURN_ALT")
        };
    }
}
