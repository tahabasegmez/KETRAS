namespace Ketras.Parameters;

/// <summary>Reads a parameter value by name; null when the vehicle does not report it.</summary>
public delegate double? ParameterReader(string name);

/// <summary>A titled set of parameters within a group.</summary>
/// <param name="Key">Stable key; the title comes from the application's text.</param>
/// <param name="Parameters">The parameters in order; those the vehicle does not report are left out when shown.</param>
public sealed record ParameterSection(string Key, IReadOnlyList<string> Parameters);

/// <summary>A cross-parameter check: the configuration is invalid while <paramref name="Fails"/> holds.</summary>
/// <param name="Key">Stable key; the message comes from the application's text.</param>
/// <param name="Parameters">The parameters it reads, so the warning shows next to them.</param>
/// <param name="Fails">True when the configuration is invalid.</param>
public sealed record ParameterCheck(string Key, IReadOnlyList<string> Parameters, Func<ParameterReader, bool> Fails);

/// <summary>A hand-picked group of parameters with its checks.</summary>
/// <param name="Key">Stable key.</param>
/// <param name="Sections">Its sections.</param>
/// <param name="Checks">Its checks.</param>
public sealed record ParameterGroup(string Key, IReadOnlyList<ParameterSection> Sections, IReadOnlyList<ParameterCheck> Checks)
{
    /// <summary>Every parameter of the group.</summary>
    public IEnumerable<string> Parameters => Sections.SelectMany(s => s.Parameters);
}

/// <summary>
/// The curated PX4 fixed-wing groups of the Parameters page (failsafes, flight envelope, tuning) and the checks PX4
/// would refuse at arming or in flight. Names are checked against the PX4 metadata and a fixed-wing SITL parameter dump.
/// </summary>
public static class Px4ParameterGroups
{
    // Declared first: the groups below use it during static initialisation.
    /// <summary>The geofence breach settings, also shown in the planner's geofence tab.</summary>
    public static IReadOnlyList<string> Geofence { get; } = ["GF_ACTION", "GF_MAX_HOR_DIST", "GF_MAX_VER_DIST", "GF_SOURCE"];

    /// <summary>Failsafe actions and limits.</summary>
    public static ParameterGroup Failsafes { get; } = new("failsafes",
    [
        new("low-battery", ["COM_LOW_BAT_ACT", "BAT_LOW_THR", "BAT_CRIT_THR", "BAT_EMERGEN_THR"]),
        new("manual-control-loss", ["NAV_RCL_ACT", "COM_RC_LOSS_T"]),
        new("data-link-loss", ["NAV_DLL_ACT", "COM_DL_LOSS_T"]),
        new("geofence", Geofence),
        new("return", ["RTL_TYPE", "RTL_RETURN_ALT", "RTL_DESCEND_ALT", "RTL_LAND_DELAY", "RTL_LOITER_RAD", "RTL_MIN_DIST", "RTL_APPR_FORCE"]),
        new("landing-disarm", ["COM_DISARM_LAND"]),
        new("attitude-failure", ["FD_FAIL_R", "FD_FAIL_R_TTRI", "FD_FAIL_P", "FD_FAIL_P_TTRI"])
    ], []);

    /// <summary>Speeds, throttle, climb and attitude limits, path following, takeoff and landing.</summary>
    public static ParameterGroup Envelope { get; } = new("envelope",
    [
        new("airspeed", ["FW_AIRSPD_STALL", "FW_AIRSPD_MIN", "FW_AIRSPD_TRIM", "FW_AIRSPD_MAX", "FW_AIRSPD_FLP_SC", "FW_USE_AIRSPD", "FW_GND_SPD_MIN",
            "FW_WIND_ARSP_SC", "FW_SERVICE_CEIL", "WEIGHT_BASE", "WEIGHT_GROSS", "WEIGHT_FUEL"]),
        new("geometry", ["FW_WING_SPAN", "FW_WING_HEIGHT"]),
        new("mission-checks", ["MIS_TKO_LAND_REQ", "MIS_DIST_1WP"]),
        new("throttle", ["FW_THR_MIN", "FW_THR_TRIM", "FW_THR_MAX", "FW_THR_IDLE", "FW_THR_ASPD_MIN", "FW_THR_ASPD_MAX", "FW_THR_SLEW_MAX"]),
        new("climb-sink", ["FW_T_CLMB_MAX", "FW_T_CLMB_R_SP", "FW_T_SINK_MIN", "FW_T_SINK_MAX", "FW_T_SINK_R_SP"]),
        new("attitude-limits", ["FW_P_LIM_MIN", "FW_P_LIM_MAX", "FW_R_LIM", "FW_MAN_P_MAX", "FW_MAN_R_MAX"]),
        new("path-following", ["NPFG_PERIOD", "NPFG_DAMPING", "NPFG_ROLL_TC", "NPFG_LB_PERIOD", "NPFG_UB_PERIOD", "NPFG_PERIOD_SF", "NPFG_SW_DST_MLT",
            "NAV_LOITER_RAD", "NAV_ACC_RAD", "NAV_FW_ALT_RAD", "FW_WP_RST_DIST"]),
        new("takeoff", ["MIS_TAKEOFF_ALT", "FW_TKO_AIRSPD", "FW_TKO_PITCH_MIN", "FW_FLAPS_TO_SCL", "RWTO_TKOFF", "RWTO_MAX_THR", "RWTO_PSP", "RWTO_RAMP_TIME",
            "RWTO_ROT_AIRSPD", "RWTO_ROT_TIME", "RWTO_NUDGE", "FW_LAUN_DETCN_ON", "FW_LAUN_AC_THLD", "FW_LAUN_AC_T", "FW_LAUN_MOT_DEL", "FW_LAUN_CS_LK_DY",
            "FW_LAUN_CLR_ALT"]),
        new("landing", ["FW_LND_ANG", "FW_LND_AIRSPD", "FW_LND_FLALT", "FW_LND_FL_PMIN", "FW_LND_FL_PMAX", "FW_LND_FL_TIME", "FW_LND_TD_TIME", "FW_LND_TD_OFF",
            "FW_LND_ABORT", "FW_LND_USETER", "FW_LND_EARLYCFG", "FW_LND_NUDGE", "FW_LND_THRTC_SC", "FW_FLAPS_LND_SCL", "FW_SPOILERS_LND", "MIS_LND_ABRT_ALT"])
    ],
    [
        new("stall-above-min", ["FW_AIRSPD_STALL", "FW_AIRSPD_MIN"], r => r("FW_AIRSPD_STALL") > r("FW_AIRSPD_MIN")),
        new("max-below-min", ["FW_AIRSPD_MAX", "FW_AIRSPD_MIN"], r => r("FW_AIRSPD_MAX") < r("FW_AIRSPD_MIN")),
        new("trim-outside", ["FW_AIRSPD_TRIM"], r => r("FW_AIRSPD_TRIM") < r("FW_AIRSPD_MIN") || r("FW_AIRSPD_TRIM") > r("FW_AIRSPD_MAX")),
        new("bank-infeasible", ["FW_AIRSPD_MAX", "FW_R_LIM"], MaxBankInfeasible)
    ]);

    /// <summary>Controller gains, edited on the ground.</summary>
    public static ParameterGroup Tuning { get; } = new("tuning",
    [
        new("roll-rate", ["FW_RR_P", "FW_RR_I", "FW_RR_D", "FW_RR_FF", "FW_RR_IMAX"]),
        new("pitch-rate", ["FW_PR_P", "FW_PR_I", "FW_PR_D", "FW_PR_FF", "FW_PR_IMAX"]),
        new("yaw-rate", ["FW_YR_P", "FW_YR_I", "FW_YR_D", "FW_YR_FF", "FW_YR_IMAX", "FW_RLL_TO_YAW_FF"]),
        new("attitude", ["FW_R_TC", "FW_P_TC", "FW_R_RMAX", "FW_P_RMAX_POS", "FW_P_RMAX_NEG", "FW_Y_RMAX", "FW_PSP_OFF"]),
        new("tecs", ["FW_T_ALT_TC", "FW_T_TAS_TC", "FW_T_HRATE_FF", "FW_T_PTCH_DAMP", "FW_T_THR_DAMPING", "FW_T_THR_INTEG", "FW_T_I_GAIN_PIT", "FW_T_RLL2THR",
            "FW_T_SPDWEIGHT", "FW_T_SEB_R_FF", "FW_T_STE_R_TC", "FW_T_VERT_ACC"]),
        new("gain-scaling", ["FW_ARSP_SCALE_EN", "FW_BAT_SCALE_EN"])
    ], []);

    /// <summary>The curated groups in page order.</summary>
    public static IReadOnlyList<ParameterGroup> All { get; } = [Failsafes, Envelope, Tuning];

    /// <summary>
    /// PX4's check that the minimum airspeed, raised by gross weight and by the load factor of the maximum bank, still
    /// fits below the maximum airspeed. The weight ratio is clamped to [0.5, 2] as PX4 does.
    /// </summary>
    private static bool MaxBankInfeasible(ParameterReader r)
    {
        if (r("FW_AIRSPD_MIN") is not { } min || r("FW_AIRSPD_MAX") is not { } max || r("FW_R_LIM") is not { } bank)
        {
            return false;
        }

        var weightBase = r("WEIGHT_BASE");
        var gross = r("WEIGHT_GROSS");
        var ratio = weightBase > 0 && gross > 0 ? Math.Clamp(gross.Value / weightBase.Value, 0.5, 2) : 1;
        return min * Math.Sqrt(ratio / Math.Cos(bank * Math.PI / 180)) > max;
    }
}
