using Ketras.Model.Geo;

namespace Ketras.Model.Vehicles;

/// <summary>Identifies a vehicle the station follows (its MAVLink system id).</summary>
/// <param name="SystemId">MAVLink system id.</param>
public readonly record struct VehicleId(int SystemId)
{
    /// <inheritdoc />
    public override string ToString() => $"vehicle {SystemId}";
}

/// <summary>Quality of the vehicle's satellite position.</summary>
public enum PositionFix
{
    /// <summary>No receiver or not reported.</summary>
    None,

    /// <summary>Receiver without a position.</summary>
    NoFix,

    /// <summary>2D position.</summary>
    Fix2D,

    /// <summary>3D position.</summary>
    Fix3D,

    /// <summary>Augmented 3D position (DGPS, RTK, PPP).</summary>
    Precise
}

/// <summary>
/// The vehicle's state at one moment, as screens show it. Immutable; the station publishes a new one at a bounded rate.
/// Null fields have not been reported.
/// </summary>
public sealed record VehicleSnapshot
{
    /// <summary>Which vehicle.</summary>
    public required VehicleId Id { get; init; }

    /// <summary>Name shown to the operator.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>When the snapshot was taken (UTC).</summary>
    public DateTime TimeUtc { get; init; }

    /// <summary>The link to the vehicle works.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Position.</summary>
    public GeoPoint? Position { get; init; }

    /// <summary>Altitude above mean sea level, metres.</summary>
    public double? AltitudeAmslMeters { get; init; }

    /// <summary>Altitude above home, metres.</summary>
    public double? RelativeAltitudeMeters { get; init; }

    /// <summary>Heading, 0…360 degrees.</summary>
    public double? HeadingDegrees { get; init; }

    /// <summary>Direction of travel over ground, 0…360 degrees; null when hardly moving.</summary>
    public double? CourseDegrees { get; init; }

    /// <summary>Ground speed, m/s.</summary>
    public double? GroundSpeedMps { get; init; }

    /// <summary>Airspeed, m/s.</summary>
    public double? AirspeedMps { get; init; }

    /// <summary>Climb rate, m/s, positive up.</summary>
    public double? ClimbRateMps { get; init; }

    /// <summary>Roll, degrees, positive right wing down.</summary>
    public double? RollDegrees { get; init; }

    /// <summary>Pitch, degrees, positive nose up.</summary>
    public double? PitchDegrees { get; init; }

    /// <summary>Armed.</summary>
    public bool IsArmed { get; init; }

    /// <summary>Autopilot-native flight mode name, e.g. "AUTO.MISSION".</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>On the ground; null when the vehicle does not report it.</summary>
    public bool? IsLanded { get; init; }

    /// <summary>Remaining battery, 0…100.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>Satellite position quality.</summary>
    public PositionFix Fix { get; init; }

    /// <summary>Satellites in view.</summary>
    public int? Satellites { get; init; }

    /// <summary>The vehicle's home.</summary>
    public GeoPoint? Home { get; init; }

    /// <summary>Altitude of the home above mean sea level.</summary>
    public double? HomeAltitudeAmslMeters { get; init; }

    /// <summary>Throttle setting, 0…100.</summary>
    public double? ThrottlePercent { get; init; }

    /// <summary>The battery beyond its percentage; null before the first report.</summary>
    public BatteryState? Battery { get; init; }

    /// <summary>The position controller's state; null before the first report.</summary>
    public NavigationState? Navigation { get; init; }

    /// <summary>Horizontal dilution of precision of the satellite position.</summary>
    public double? Hdop { get; init; }

    /// <summary>The estimator's test ratios; null before the first report.</summary>
    public EstimatorHealth? Estimator { get; init; }

    /// <summary>Vibration; null before the first report.</summary>
    public VibrationState? Vibration { get; init; }

    /// <summary>When the vehicle armed (UTC), as the station saw it; null while disarmed.</summary>
    public DateTime? ArmedSinceUtc { get; init; }
}

/// <summary>The battery as the autopilot reports it; null fields are not reported.</summary>
/// <param name="VoltageVolts">Pack voltage.</param>
/// <param name="CurrentAmps">Current draw.</param>
/// <param name="ConsumedMah">Charge used since the autopilot started.</param>
/// <param name="TimeRemainingSeconds">The autopilot's estimate of the time left.</param>
/// <param name="CellCount">Cells the voltage was measured over; null or 1 when only the pack is measured.</param>
public sealed record BatteryState(double? VoltageVolts, double? CurrentAmps, double? ConsumedMah, double? TimeRemainingSeconds, int? CellCount);

/// <summary>
/// The position controller's state (MAVLink NAV_CONTROLLER_OUTPUT).
/// </summary>
/// <param name="WaypointDistanceMeters">Distance to the active waypoint.</param>
/// <param name="TargetBearingDegrees">Bearing to the active waypoint.</param>
/// <param name="AltitudeErrorMeters">Altitude setpoint minus altitude: positive while below the target.</param>
/// <param name="AirspeedErrorMps">Airspeed minus its setpoint: negative while too slow.</param>
/// <param name="CrossTrackErrorMeters">Distance off the line to the active waypoint.</param>
public sealed record NavigationState(
    double WaypointDistanceMeters,
    double TargetBearingDegrees,
    double AltitudeErrorMeters,
    double AirspeedErrorMps,
    double CrossTrackErrorMeters);

/// <summary>The estimator's innovation test ratios: below 0.5 good, 0.5…1 marginal, 1 and above rejected.</summary>
/// <param name="Velocity">Velocity.</param>
/// <param name="HorizontalPosition">Horizontal position.</param>
/// <param name="VerticalPosition">Vertical position.</param>
/// <param name="Magnetometer">Magnetometer.</param>
/// <param name="HeightAboveGround">Height above ground.</param>
/// <param name="Airspeed">True airspeed.</param>
public sealed record EstimatorHealth(double Velocity, double HorizontalPosition, double VerticalPosition, double Magnetometer,
    double HeightAboveGround, double Airspeed)
{
    /// <summary>The highest ratio and what it measures.</summary>
    public (string Name, double Ratio) Worst =>
        new[]
        {
            ("velocity", Velocity), ("horizontal position", HorizontalPosition), ("vertical position", VerticalPosition),
            ("magnetometer", Magnetometer), ("height above ground", HeightAboveGround), ("airspeed", Airspeed)
        }.MaxBy(r => double.IsFinite(r.Item2) ? r.Item2 : 0);
}

/// <summary>Vibration of the accelerometers.</summary>
/// <param name="LevelMps2">The highest axis level, m/s².</param>
/// <param name="Clipping">Clipping events since the autopilot started.</param>
public sealed record VibrationState(double LevelMps2, long Clipping);

/// <summary>How the uploaded mission is going.</summary>
public enum MissionExecution
{
    /// <summary>Not flying the mission (disarmed).</summary>
    Idle,

    /// <summary>Flying the mission.</summary>
    Executing,

    /// <summary>Armed, in another mode.</summary>
    Paused
}

/// <summary>Progress of the uploaded mission, mapped back to the plan's items.</summary>
/// <param name="State">Execution state.</param>
/// <param name="CurrentItemIndex">Index in the plan of the item being flown; null when unknown.</param>
/// <param name="Detail">Short text, e.g. the flight mode.</param>
public sealed record MissionProgress(MissionExecution State, int? CurrentItemIndex = null, string? Detail = null)
{
    /// <summary>No mission running.</summary>
    public static MissionProgress Idle { get; } = new(MissionExecution.Idle);
}

/// <summary>The vehicle's live clearance over terrain.</summary>
/// <param name="AboveGroundMeters">Height above the terrain below the vehicle; null when unknown.</param>
/// <param name="ConflictAheadSeconds">Seconds until the predicted path meets terrain; null when none is predicted.</param>
/// <param name="Message">Short detail, e.g. "no terrain data here".</param>
/// <param name="IsWarning">A conflict is predicted.</param>
public sealed record TerrainClearance(double? AboveGroundMeters, double? ConflictAheadSeconds, string? Message, bool IsWarning);
