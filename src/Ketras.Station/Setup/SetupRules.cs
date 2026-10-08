using Ketras.Station.Parameters;

namespace Ketras.Station.Setup;

/// <summary>A setup task's state.</summary>
public enum SetupState
{
    /// <summary>Not applicable: the vehicle reports none of its parameters.</summary>
    None,

    /// <summary>Done.</summary>
    Ready,

    /// <summary>Still to do.</summary>
    Required
}

/// <summary>
/// "Setup required" rules per setup task, PX4 vehicle components (AirframeComponent,
/// SensorsComponent, PX4RadioComponent, PowerComponent). Pure functions of the vehicle's parameters.
/// </summary>
public static class SetupRules
{
    /// <summary>The fixed-wing value of <c>CA_AIRFRAME</c>.</summary>
    public const int FixedWingAirframe = 1;

    /// <summary>An airframe is selected (<c>SYS_AUTOSTART</c> not 0).</summary>
    public static SetupState Airframe(ParameterReader r) =>
        r("SYS_AUTOSTART") is { } id ? id != 0 ? SetupState.Ready : SetupState.Required : SetupState.None;

    /// <summary>The vehicle is set up as something other than a fixed-wing.</summary>
    public static bool IsNotFixedWing(ParameterReader r) => r("CA_AIRFRAME") is { } type && type != FixedWingAirframe;

    /// <summary>Every sensor the vehicle has is calibrated.</summary>
    public static SetupState Sensors(ParameterReader r) =>
        r("CAL_GYRO0_ID") is null ? SetupState.None
        : GyroRequired(r) || AccelerometerRequired(r) || CompassRequired(r) || AirspeedRequired(r) ? SetupState.Required
        : SetupState.Ready;

    /// <summary>The gyroscope is not calibrated.</summary>
    public static bool GyroRequired(ParameterReader r) => r("CAL_GYRO0_ID") == 0;

    /// <summary>The accelerometer is not calibrated.</summary>
    public static bool AccelerometerRequired(ParameterReader r) => r("CAL_ACC0_ID") == 0;

    /// <summary>The vehicle uses a compass.</summary>
    public static bool HasCompass(ParameterReader r) => r("SYS_HAS_MAG") is not 0;

    /// <summary>The compass is used and not calibrated.</summary>
    public static bool CompassRequired(ParameterReader r) => HasCompass(r) && r("CAL_MAG0_ID") == 0;

    /// <summary>The vehicle has an airspeed sensor.</summary>
    public static bool HasAirspeed(ParameterReader r) => r("SYS_HAS_NUM_ASPD") is > 0;

    /// <summary>The airspeed sensor has no offset yet.</summary>
    public static bool AirspeedRequired(ParameterReader r) => HasAirspeed(r) && r("SENS_DPRES_OFF") == 0;

    /// <summary>RC is used: not when manual control comes over MAVLink only (1) or is off (4).</summary>
    public static bool RadioUsed(ParameterReader r) => r("COM_RC_IN_MODE") is not (1 or 4);

    /// <summary>The sticks are mapped, or RC is not used.</summary>
    public static SetupState Radio(ParameterReader r) =>
        r("RC_MAP_ROLL") is null ? SetupState.None
        : !RadioUsed(r) ? SetupState.Ready
        : new[] { "RC_MAP_ROLL", "RC_MAP_PITCH", "RC_MAP_YAW", "RC_MAP_THROTTLE" }.Any(p => r(p) is 0) ? SetupState.Required
        : SetupState.Ready;

    /// <summary>Battery 1 is off or has its cells and voltages.</summary>
    public static SetupState Power(ParameterReader r) =>
        r("BAT1_SOURCE") is not { } source ? SetupState.None
        : source < 0 ? SetupState.Ready
        : r("BAT1_V_CHARGED") is not 0 && r("BAT1_V_EMPTY") is not 0 && r("BAT1_N_CELLS") is not 0 ? SetupState.Ready
        : SetupState.Required;
}
