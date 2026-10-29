namespace Ketras.Vehicles;

/// <summary>How a check came out.</summary>
public enum CheckLevel
{
    /// <summary>Not known yet (no data).</summary>
    Unknown,

    /// <summary>Fine.</summary>
    Pass,

    /// <summary>Flyable, look at it.</summary>
    Warning,

    /// <summary>Do not fly like this.</summary>
    Error
}

/// <summary>One check of the vehicle or the flight.</summary>
/// <param name="Code">Stable identifier, e.g. "vehicle.gps".</param>
/// <param name="Title">What is checked, e.g. "GPS".</param>
/// <param name="Level">The outcome.</param>
/// <param name="Detail">The values behind it, e.g. "3D fix, 14 satellites, HDOP 0.8".</param>
public sealed record FlightCheck(string Code, string Title, CheckLevel Level, string Detail);

/// <summary>The link's recent quality.</summary>
/// <param name="LossPercent">Packets lost over the last seconds, %; null before enough traffic.</param>
/// <param name="Rssi">The telemetry radio's local signal strength (device units, SiK about 2 × dB); null without a radio.</param>
/// <param name="RemoteRssi">The radio's remote signal strength.</param>
public sealed record LinkQuality(double? LossPercent, int? Rssi, int? RemoteRssi);

/// <summary>The battery's configured voltages (PX4 BAT1_N_CELLS, BAT1_V_EMPTY, BAT1_V_CHARGED).</summary>
/// <param name="Cells">Cells in series.</param>
/// <param name="EmptyVolts">A cell's empty voltage.</param>
/// <param name="ChargedVolts">A cell's full voltage.</param>
public sealed record BatteryLimits(int Cells, double EmptyVolts, double ChargedVolts);

/// <summary>
/// Rules: the vehicle's health from its telemetry, the same before and during flight. Thresholds: estimator test
/// ratios 0.5 / 1; vibration 30 / 60 m/s² or new clipping; position 3D fix, 6 satellites, HDOP 2; link loss 10 / 30 %; cell voltage at a quarter of the
/// way from empty to full, and empty.
/// </summary>
public static class FlightHealthRules
{
    /// <summary>Fewer satellites than this is a warning.</summary>
    public const int MinSatellites = 6;

    /// <summary>A higher HDOP is a warning.</summary>
    public const double MaxHdop = 2.0;

    /// <summary>Estimator ratio warning and error.</summary>
    public const double EstimatorWarning = 0.5, EstimatorError = 1.0;

    /// <summary>Vibration warning and error, m/s².</summary>
    public const double VibrationWarning = 30, VibrationError = 60;

    /// <summary>Link loss warning and error, %.</summary>
    public const double LossWarning = 10, LossError = 30;

    /// <summary>The checks for <paramref name="snapshot"/>.</summary>
    /// <param name="snapshot">The vehicle now.</param>
    /// <param name="link">The link's quality; null when unknown.</param>
    /// <param name="battery">The battery's configured voltages; null when unknown.</param>
    /// <param name="newClipping">Clipping events seen over the last seconds.</param>
    public static IReadOnlyList<FlightCheck> Evaluate(VehicleSnapshot snapshot, LinkQuality? link, BatteryLimits? battery, long newClipping) =>
        [Gps(snapshot), Estimator(snapshot), Vibration(snapshot, newClipping), Battery(snapshot, battery), Link(link)];

    private static FlightCheck Gps(VehicleSnapshot s)
    {
        var detail = $"{FixName(s.Fix)}, {(s.Satellites is { } n ? $"{n} satellites" : "satellites unknown")}{(s.Hdop is { } h ? $", HDOP {h:F1}" : string.Empty)}";
        var level = s.Fix == PositionFix.None && s.Satellites is null ? CheckLevel.Unknown
            : s.Fix is not (PositionFix.Fix3D or PositionFix.Precise) ? CheckLevel.Error
            : s.Satellites < MinSatellites || s.Hdop > MaxHdop ? CheckLevel.Warning
            : CheckLevel.Pass;
        return new FlightCheck("vehicle.gps", "GPS", level, detail);
    }

    private static FlightCheck Estimator(VehicleSnapshot s)
    {
        if (s.Estimator is not { } estimator)
        {
            return new FlightCheck("vehicle.estimator", "Estimator", CheckLevel.Unknown, "no report yet");
        }

        var (name, ratio) = estimator.Worst;
        var level = ratio >= EstimatorError ? CheckLevel.Error : ratio >= EstimatorWarning ? CheckLevel.Warning : CheckLevel.Pass;
        return new FlightCheck("vehicle.estimator", "Estimator", level, $"worst test ratio {ratio:F2} ({name})");
    }

    private static FlightCheck Vibration(VehicleSnapshot s, long newClipping)
    {
        if (s.Vibration is not { } vibration)
        {
            return new FlightCheck("vehicle.vibration", "Vibration", CheckLevel.Unknown, "no report yet");
        }

        var level = vibration.LevelMps2 >= VibrationError ? CheckLevel.Error
            : vibration.LevelMps2 >= VibrationWarning || newClipping > 0 ? CheckLevel.Warning
            : CheckLevel.Pass;
        return new FlightCheck("vehicle.vibration", "Vibration", level,
            $"{vibration.LevelMps2:F1} m/s²{(newClipping > 0 ? $", {newClipping} new clipping events" : string.Empty)}");
    }

    private static FlightCheck Battery(VehicleSnapshot s, BatteryLimits? limits)
    {
        if (s.Battery?.VoltageVolts is not { } volts)
        {
            return new FlightCheck("vehicle.battery", "Battery", CheckLevel.Unknown, "no voltage reported");
        }

        var percent = s.BatteryPercent is { } p ? $", {p} %" : string.Empty;
        if (limits is not { Cells: > 0 } l || l.ChargedVolts <= l.EmptyVolts)
        {
            return new FlightCheck("vehicle.battery", "Battery", CheckLevel.Pass, $"{volts:F2} V{percent}");
        }

        var cell = volts / l.Cells;
        var level = cell <= l.EmptyVolts ? CheckLevel.Error
            : cell < l.EmptyVolts + ((l.ChargedVolts - l.EmptyVolts) / 4) ? CheckLevel.Warning
            : CheckLevel.Pass;
        return new FlightCheck("vehicle.battery", "Battery", level, $"{volts:F2} V, {cell:F2} V per cell{percent}");
    }

    private static FlightCheck Link(LinkQuality? link)
    {
        if (link?.LossPercent is not { } loss)
        {
            return new FlightCheck("vehicle.link", "Link", CheckLevel.Unknown, "not measured yet");
        }

        var level = loss >= LossError ? CheckLevel.Error : loss >= LossWarning ? CheckLevel.Warning : CheckLevel.Pass;
        var radio = link.Rssi is { } rssi ? $", RSSI {rssi}/{link.RemoteRssi}" : string.Empty;
        return new FlightCheck("vehicle.link", "Link", level, $"{loss:F0} % lost{radio}");
    }

    private static string FixName(PositionFix fix) => fix switch
    {
        PositionFix.Precise => "precise fix",
        PositionFix.Fix3D => "3D fix",
        PositionFix.Fix2D => "2D fix",
        PositionFix.NoFix => "no fix",
        _ => "no receiver"
    };
}
