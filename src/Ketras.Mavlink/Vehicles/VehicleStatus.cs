using Ketras.Mavlink.Autopilots;
using Ketras.Mavlink.Framing;
using Ketras.Mavlink.Messages;
using Microsoft.Extensions.Logging;

namespace Ketras.Mavlink.Vehicles;

/// <summary>Arming, flight mode and landed state as last reported.</summary>
/// <param name="IsArmed">The vehicle is armed.</param>
/// <param name="Mode">Autopilot-native mode name, e.g. "AUTO.MISSION"; empty until the first heartbeat.</param>
/// <param name="Landed">Landed state from EXTENDED_SYS_STATE; null when the vehicle does not report one.</param>
/// <param name="SystemStatus">MAV_STATE from the heartbeat.</param>
public sealed record VehicleState(bool IsArmed, string Mode, MavLandedState? Landed, MavState SystemStatus)
{
    /// <summary>True when the vehicle reports it is on the ground; null when it does not report a landed state.</summary>
    public bool? IsLanded => Landed switch
    {
        null or MavLandedState.Undefined => null,
        MavLandedState.OnGround => true,
        _ => false
    };
}

/// <summary>Global position and motion. Altitudes in metres; <see cref="HeadingDegrees"/> null when unknown;
/// <see cref="CourseDegrees"/> (direction of travel over ground) null below 1 m/s.</summary>
/// <param name="Latitude">WGS84 degrees.</param>
/// <param name="Longitude">WGS84 degrees.</param>
/// <param name="AltitudeAmsl">Altitude above mean sea level, metres.</param>
/// <param name="RelativeAltitude">Altitude above home, metres.</param>
/// <param name="HeadingDegrees">Vehicle heading, 0…360.</param>
/// <param name="GroundSpeed">Horizontal speed over ground, m/s.</param>
/// <param name="Airspeed">Airspeed from VFR_HUD, m/s.</param>
/// <param name="ClimbRate">Climb rate from VFR_HUD, m/s, positive up.</param>
/// <param name="CourseDegrees">Direction of travel over ground, 0…360.</param>
/// <param name="ThrottlePercent">Throttle setting from VFR_HUD, 0…100; null before the first VFR_HUD.</param>
public sealed record VehiclePosition(
    double Latitude,
    double Longitude,
    double AltitudeAmsl,
    double RelativeAltitude,
    double? HeadingDegrees,
    double GroundSpeed,
    double? Airspeed,
    double ClimbRate,
    double? CourseDegrees,
    double? ThrottlePercent = null);

/// <summary>Attitude from ATTITUDE, in degrees: roll right wing down, pitch nose up, yaw from north (0…360).</summary>
/// <param name="RollDegrees">Roll, positive right wing down.</param>
/// <param name="PitchDegrees">Pitch, positive nose up.</param>
/// <param name="YawDegrees">Yaw from north, 0…360.</param>
public sealed record VehicleAttitude(double RollDegrees, double PitchDegrees, double YawDegrees);

/// <summary>Home position in WGS84, altitude AMSL in metres.</summary>
/// <param name="Latitude">WGS84 degrees.</param>
/// <param name="Longitude">WGS84 degrees.</param>
/// <param name="AltitudeAmsl">Metres above mean sea level.</param>
public sealed record HomePosition(double Latitude, double Longitude, double AltitudeAmsl);

/// <summary>Battery reading from BATTERY_STATUS; null fields were not reported.</summary>
/// <param name="VoltageVolts">Sum of the cell voltages.</param>
/// <param name="CurrentAmps">Current draw.</param>
/// <param name="RemainingPercent">Remaining capacity, 0…100.</param>
/// <param name="ConsumedMah">Charge used since the autopilot started.</param>
/// <param name="TimeRemainingSeconds">The autopilot's estimate of the time left.</param>
/// <param name="TemperatureCelsius">Battery temperature.</param>
/// <param name="CellCount">Cells reported with a voltage (PX4 puts the pack voltage in the first cell when it does not
/// measure cells, so 1 can mean "unknown").</param>
public sealed record BatteryReading(
    double? VoltageVolts,
    double? CurrentAmps,
    int? RemainingPercent,
    double? ConsumedMah = null,
    double? TimeRemainingSeconds = null,
    double? TemperatureCelsius = null,
    int? CellCount = null);

/// <summary>
/// The position controller's state from NAV_CONTROLLER_OUTPUT. PX4 fills it from position_controller_status and
/// tecs_status (streams/NAV_CONTROLLER_OUTPUT.hpp): <see cref="AltitudeErrorMeters"/> is setpoint − altitude (positive:
/// below the target), <see cref="AirspeedErrorMps"/> is airspeed − setpoint (negative: too slow).
/// </summary>
/// <param name="WaypointDistanceMeters">Distance to the active waypoint.</param>
/// <param name="TargetBearingDegrees">Bearing to the active waypoint, 0…360.</param>
/// <param name="AltitudeErrorMeters">Altitude setpoint minus altitude.</param>
/// <param name="AirspeedErrorMps">Airspeed minus airspeed setpoint.</param>
/// <param name="CrossTrackErrorMeters">Distance off the line between the previous and the active waypoint.</param>
public sealed record NavigationReport(
    double WaypointDistanceMeters,
    double TargetBearingDegrees,
    double AltitudeErrorMeters,
    double AirspeedErrorMps,
    double CrossTrackErrorMeters);

/// <summary>
/// The estimator's innovation test ratios from ESTIMATOR_STATUS: below 0.5 is good, 0.5…1 is marginal, 1 and above is a
/// rejected measurement.
/// </summary>
/// <param name="Flags">ESTIMATOR_STATUS_FLAGS.</param>
/// <param name="Velocity">Velocity test ratio.</param>
/// <param name="HorizontalPosition">Horizontal position test ratio.</param>
/// <param name="VerticalPosition">Vertical position test ratio.</param>
/// <param name="Magnetometer">Magnetometer test ratio.</param>
/// <param name="HeightAboveGround">Height above ground test ratio.</param>
/// <param name="Airspeed">True airspeed test ratio.</param>
public sealed record EstimatorReport(ushort Flags, double Velocity, double HorizontalPosition, double VerticalPosition, double Magnetometer,
    double HeightAboveGround, double Airspeed);

/// <summary>Vibration from VIBRATION: the accelerometers' vibration levels (m/s²) and their clipping counts since boot.</summary>
/// <param name="X">Level on the X axis.</param>
/// <param name="Y">Level on the Y axis.</param>
/// <param name="Z">Level on the Z axis.</param>
/// <param name="Clipping">Clipping events of the first three accelerometers, summed.</param>
public sealed record VibrationReport(double X, double Y, double Z, long Clipping);

/// <summary>Firmware identity from AUTOPILOT_VERSION: "major.minor.patch" plus a release type suffix when not an
/// official release, and the source revision when the autopilot reports one.</summary>
/// <param name="Version">E.g. "1.15.0" or "1.16.0 beta".</param>
/// <param name="GitHash">Source revision, when reported.</param>
public sealed record FirmwareVersion(string Version, string? GitHash);

/// <summary>GNSS fix quality (GPS_FIX_TYPE).</summary>
public enum GpsFix
{
    /// <summary>No GNSS receiver connected.</summary>
    NoGps,

    /// <summary>Receiver connected, no position.</summary>
    NoFix,

    /// <summary>2D position.</summary>
    Fix2D,

    /// <summary>3D position.</summary>
    Fix3D,

    /// <summary>DGPS/SBAS-aided 3D position.</summary>
    Dgps,

    /// <summary>RTK float.</summary>
    RtkFloat,

    /// <summary>RTK fixed.</summary>
    RtkFixed,

    /// <summary>Static fixed, typically a base station.</summary>
    Static,

    /// <summary>Precise point positioning.</summary>
    Ppp
}

/// <summary>GNSS receiver state from GPS_RAW_INT.</summary>
/// <param name="Fix">Fix quality.</param>
/// <param name="SatellitesVisible">Satellites in view; null when unknown.</param>
/// <param name="Hdop">Horizontal dilution of precision; null when unknown.</param>
/// <param name="Vdop">Vertical dilution of precision; null when unknown.</param>
public sealed record GpsStatus(GpsFix Fix, int? SatellitesVisible, double? Hdop, double? Vdop);

/// <summary>
/// Wind estimate from WIND_COV: the air's velocity in NED, m/s. <see cref="FromDegrees"/> is the meteorological
/// direction the wind blows from; the vector itself points where the air goes.
/// </summary>
/// <param name="NorthMps">Air velocity towards north.</param>
/// <param name="EastMps">Air velocity towards east.</param>
/// <param name="DownMps">Air velocity downwards; null when not estimated.</param>
public sealed record WindEstimate(double NorthMps, double EastMps, double? DownMps)
{
    /// <summary>Horizontal wind speed, m/s.</summary>
    public double SpeedMps => Math.Sqrt((NorthMps * NorthMps) + (EastMps * EastMps));

    /// <summary>Direction the wind blows from, 0…360 degrees from north.</summary>
    public double FromDegrees => ((Math.Atan2(EastMps, NorthMps) * 180 / Math.PI) + 180 + 360) % 360;
}

/// <summary>Ambient air from HYGROMETER_SENSOR.</summary>
/// <param name="TemperatureCelsius">Air temperature.</param>
/// <param name="HumidityPercent">Relative humidity, 0…100.</param>
public sealed record AirSample(double TemperatureCelsius, double HumidityPercent);

/// <summary>
/// Observer subject: turns the vehicle's telemetry into typed state and change events. Events are raised on the
/// connection's dispatch thread, one subscriber at a time; a subscriber that throws is logged and skipped.
/// </summary>
public sealed class VehicleStatus
{
    private readonly IAutopilotProfile _profile;
    private readonly ILogger _logger;
    private double? _airspeed;
    private double _climb;
    private double? _throttle;
    private int _missionTotal = -1;
    private readonly StatusTextAssembler _texts = new();

    internal VehicleStatus(IAutopilotProfile profile, ILogger logger)
    {
        _profile = profile;
        _logger = logger;
        State = new VehicleState(false, string.Empty, null, MavState.Uninit);
    }

    /// <summary>Latest arming / mode / landed state.</summary>
    public VehicleState State { get; private set; }

    /// <summary>Latest position, null until the first GLOBAL_POSITION_INT.</summary>
    public VehiclePosition? Position { get; private set; }

    /// <summary>Latest attitude, null until the first ATTITUDE.</summary>
    public VehicleAttitude? Attitude { get; private set; }

    /// <summary>Latest home, null until HOME_POSITION arrives.</summary>
    public HomePosition? Home { get; private set; }

    /// <summary>Current mission sequence number as reported by MISSION_CURRENT; -1 until known.</summary>
    public int MissionCurrent { get; private set; } = -1;

    /// <summary>Firmware identity, null until AUTOPILOT_VERSION arrives (requested on discovery).</summary>
    public FirmwareVersion? Firmware { get; private set; }

    /// <summary>GNSS receiver state, null until the first GPS_RAW_INT.</summary>
    public GpsStatus? Gps { get; private set; }

    /// <summary>Wind estimate, null until the first WIND_COV with a valid estimate.</summary>
    public WindEstimate? Wind { get; private set; }

    /// <summary>Ambient air, null until the first HYGROMETER_SENSOR.</summary>
    public AirSample? Air { get; private set; }

    /// <summary>Latest battery reading, null until the first BATTERY_STATUS.</summary>
    public BatteryReading? BatteryState { get; private set; }

    /// <summary>The position controller's state, null until the first NAV_CONTROLLER_OUTPUT.</summary>
    public NavigationReport? Navigation { get; private set; }

    /// <summary>Estimator test ratios, null until the first ESTIMATOR_STATUS.</summary>
    public EstimatorReport? Estimator { get; private set; }

    /// <summary>Vibration levels, null until the first VIBRATION.</summary>
    public VibrationReport? Vibration { get; private set; }

    /// <summary>Every NAV_CONTROLLER_OUTPUT.</summary>
    public event Action<NavigationReport>? NavigationChanged;

    /// <summary>Every ESTIMATOR_STATUS.</summary>
    public event Action<EstimatorReport>? EstimatorChanged;

    /// <summary>Every VIBRATION.</summary>
    public event Action<VibrationReport>? VibrationChanged;

    /// <summary>AUTOPILOT_VERSION received or changed.</summary>
    public event Action<FirmwareVersion>? FirmwareChanged;

    /// <summary>State changed (armed, mode, landed or system status).</summary>
    public event Action<VehicleState>? StateChanged;

    /// <summary>Every GLOBAL_POSITION_INT.</summary>
    public event Action<VehiclePosition>? PositionChanged;

    /// <summary>Every ATTITUDE.</summary>
    public event Action<VehicleAttitude>? AttitudeChanged;

    /// <summary>Home position reported or changed.</summary>
    public event Action<HomePosition>? HomeChanged;

    /// <summary>Mission sequence and total item count (total -1 when not reported).</summary>
    public event Action<int, int>? MissionCurrentChanged;

    /// <summary>STATUSTEXT from the vehicle: severity and the whole text (chunks joined).</summary>
    public event Action<MavSeverity, string>? StatusText;

    /// <summary>RC_CHANNELS raw PWM values, one per reported channel.</summary>
    public event Action<IReadOnlyList<ushort>>? RcChannels;

    /// <summary>Battery reading from BATTERY_STATUS (battery 0).</summary>
    public event Action<BatteryReading>? Battery;

    /// <summary>GNSS fix quality, satellites or dilution changed.</summary>
    public event Action<GpsStatus>? GpsChanged;

    /// <summary>Every WIND_COV with a valid estimate.</summary>
    public event Action<WindEstimate>? WindChanged;

    /// <summary>Every HYGROMETER_SENSOR.</summary>
    public event Action<AirSample>? AirChanged;

    /// <summary>Finishes a chunked text that never got its last chunk; called on the dispatch thread by the link check.</summary>
    internal void ExpireTexts(DateTime nowUtc)
    {
        if (_texts.Expire(nowUtc) is { } text)
        {
            Subscribers.Raise(StatusText, text.Severity, text.Text, _logger, nameof(StatusText));
        }
    }

    internal void Handle(MavlinkPacket packet, byte autopilotComponent)
    {
        switch (packet.Message)
        {
            case HeartbeatMessage hb when packet.ComponentId == autopilotComponent:
                UpdateState(State with
                {
                    IsArmed = (hb.BaseMode & (byte)MavModeFlag.SafetyArmed) != 0,
                    Mode = _profile.ModeName(hb.BaseMode, hb.CustomMode),
                    SystemStatus = (MavState)hb.SystemStatus
                });
                break;

            case ExtendedSysStateMessage ext:
                UpdateState(State with { Landed = (MavLandedState)ext.LandedState });
                break;

            case VfrHudMessage hud:
                _airspeed = hud.Airspeed;
                _climb = hud.Climb;
                _throttle = hud.Throttle;
                break;

            case GlobalPositionIntMessage gp:
                var groundSpeed = Math.Sqrt((gp.Vx * (double)gp.Vx) + (gp.Vy * (double)gp.Vy)) / 100.0;
                var position = new VehiclePosition(
                    gp.Lat / 1e7,
                    gp.Lon / 1e7,
                    gp.Alt / 1000.0,
                    gp.RelativeAlt / 1000.0,
                    gp.Hdg == ushort.MaxValue ? null : gp.Hdg / 100.0,
                    groundSpeed,
                    _airspeed,
                    _climb,
                    groundSpeed < 1 ? null : ((Math.Atan2(gp.Vy, gp.Vx) * 180 / Math.PI) + 360) % 360,
                    _throttle);
                Position = position;
                Subscribers.Raise(PositionChanged, position, _logger, nameof(PositionChanged));
                break;

            case AttitudeMessage attitude when packet.ComponentId == autopilotComponent:
                const double degrees = 180 / Math.PI;
                var current = new VehicleAttitude(attitude.Roll * degrees, attitude.Pitch * degrees, ((attitude.Yaw * degrees) + 360) % 360);
                Attitude = current;
                Subscribers.Raise(AttitudeChanged, current, _logger, nameof(AttitudeChanged));
                break;

            case HomePositionMessage home:
                var h = new HomePosition(home.Latitude / 1e7, home.Longitude / 1e7, home.Altitude / 1000.0);
                if (h != Home)
                {
                    Home = h;
                    Subscribers.Raise(HomeChanged, h, _logger, nameof(HomeChanged));
                }

                break;

            case MissionCurrentMessage missionCurrent:
                var total = missionCurrent.Total == ushort.MaxValue ? -1 : missionCurrent.Total;
                if (missionCurrent.Seq != MissionCurrent || total != _missionTotal)
                {
                    MissionCurrent = missionCurrent.Seq;
                    _missionTotal = total;
                    Subscribers.Raise(MissionCurrentChanged, (int)missionCurrent.Seq, total, _logger, nameof(MissionCurrentChanged));
                }

                break;

            case AutopilotVersionMessage version when packet.ComponentId == autopilotComponent:
                var firmware = new FirmwareVersion(VersionText(version.FlightSwVersion), _profile.DecodeGitHash(version.FlightCustomVersion));
                if (firmware != Firmware)
                {
                    Firmware = firmware;
                    Subscribers.Raise(FirmwareChanged, firmware, _logger, nameof(FirmwareChanged));
                }

                break;

            case StatustextMessage text:
                foreach (var (severity, whole) in _texts.Add(text, DateTime.UtcNow))
                {
                    Subscribers.Raise(StatusText, severity, whole, _logger, nameof(StatusText));
                }

                break;

            case RcChannelsMessage rc:
                Subscribers.Raise<IReadOnlyList<ushort>>(RcChannels, RcValues(rc), _logger, nameof(RcChannels));
                break;

            case BatteryStatusMessage battery when battery.Id == 0:
                var measured = battery.Voltages.Where(v => v != ushort.MaxValue).ToList();
                var cells = measured.Sum(v => (double)v);
                var reading = new BatteryReading(
                    cells > 0 ? cells / 1000.0 : null,
                    battery.CurrentBattery < 0 ? null : battery.CurrentBattery / 100.0,
                    battery.BatteryRemaining < 0 ? null : battery.BatteryRemaining,
                    battery.CurrentConsumed < 0 ? null : battery.CurrentConsumed,
                    battery.TimeRemaining <= 0 ? null : battery.TimeRemaining,
                    battery.Temperature == short.MaxValue ? null : battery.Temperature / 100.0,
                    measured.Count == 0 ? null : measured.Count);
                BatteryState = reading;
                Subscribers.Raise(Battery, reading, _logger, nameof(Battery));
                break;

            case NavControllerOutputMessage nav:
                var navigation = new NavigationReport(nav.WpDist, ((nav.TargetBearing % 360) + 360) % 360, nav.AltError, nav.AspdError, nav.XtrackError);
                Navigation = navigation;
                Subscribers.Raise(NavigationChanged, navigation, _logger, nameof(NavigationChanged));
                break;

            case EstimatorStatusMessage estimator:
                var ratios = new EstimatorReport(estimator.Flags, estimator.VelRatio, estimator.PosHorizRatio, estimator.PosVertRatio,
                    estimator.MagRatio, estimator.HaglRatio, estimator.TasRatio);
                Estimator = ratios;
                Subscribers.Raise(EstimatorChanged, ratios, _logger, nameof(EstimatorChanged));
                break;

            case VibrationMessage vibration:
                var levels = new VibrationReport(vibration.VibrationX, vibration.VibrationY, vibration.VibrationZ,
                    (long)vibration.Clipping0 + vibration.Clipping1 + vibration.Clipping2);
                Vibration = levels;
                Subscribers.Raise(VibrationChanged, levels, _logger, nameof(VibrationChanged));
                break;

            case GpsRawIntMessage gps:
                var gpsStatus = new GpsStatus(
                    FixOf(gps.FixType),
                    gps.SatellitesVisible == byte.MaxValue ? null : gps.SatellitesVisible,
                    gps.Eph == ushort.MaxValue ? null : gps.Eph / 100.0,
                    gps.Epv == ushort.MaxValue ? null : gps.Epv / 100.0);
                if (gpsStatus != Gps)
                {
                    Gps = gpsStatus;
                    Subscribers.Raise(GpsChanged, gpsStatus, _logger, nameof(GpsChanged));
                }

                break;

            case WindCovMessage wind when float.IsFinite(wind.WindX) && float.IsFinite(wind.WindY):
                var estimate = new WindEstimate(wind.WindX, wind.WindY, float.IsFinite(wind.WindZ) ? wind.WindZ : null);
                Wind = estimate;
                Subscribers.Raise(WindChanged, estimate, _logger, nameof(WindChanged));
                break;

            case HygrometerSensorMessage air:
                // common.xml declares humidity in c%, but PX4 sends whole percent (streams/HYGROMETER_SENSOR.hpp:73)
                // and is read that way.
                var sample = new AirSample(air.Temperature / 100.0, air.Humidity);
                Air = sample;
                Subscribers.Raise(AirChanged, sample, _logger, nameof(AirChanged));
                break;
        }
    }

    private void UpdateState(VehicleState next)
    {
        if (next == State)
        {
            return;
        }

        State = next;
        Subscribers.Raise(StateChanged, next, _logger, nameof(StateChanged));
    }

    private static GpsFix FixOf(byte fixType) => fixType switch
    {
        1 => GpsFix.NoFix,
        2 => GpsFix.Fix2D,
        3 => GpsFix.Fix3D,
        4 => GpsFix.Dgps,
        5 => GpsFix.RtkFloat,
        6 => GpsFix.RtkFixed,
        7 => GpsFix.Static,
        8 => GpsFix.Ppp,
        _ => GpsFix.NoGps
    };

    /// <summary>flight_sw_version: major, minor, patch, FIRMWARE_VERSION_TYPE from the top byte down.</summary>
    private static string VersionText(uint packed)
    {
        var number = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{(packed >> 24) & 0xFF}.{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}");
        return (FirmwareVersionType)(packed & 0xFF) switch
        {
            FirmwareVersionType.Official => number,
            FirmwareVersionType.Dev => number + " dev",
            FirmwareVersionType.Alpha => number + " alpha",
            FirmwareVersionType.Beta => number + " beta",
            FirmwareVersionType.Rc => number + " rc",
            _ => number
        };
    }

    private static ushort[] RcValues(RcChannelsMessage rc)
    {
        ushort[] all =
        [
            rc.Chan1Raw, rc.Chan2Raw, rc.Chan3Raw, rc.Chan4Raw, rc.Chan5Raw, rc.Chan6Raw,
            rc.Chan7Raw, rc.Chan8Raw, rc.Chan9Raw, rc.Chan10Raw, rc.Chan11Raw, rc.Chan12Raw,
            rc.Chan13Raw, rc.Chan14Raw, rc.Chan15Raw, rc.Chan16Raw, rc.Chan17Raw, rc.Chan18Raw
        ];
        return all.Take(Math.Min((int)rc.Chancount, all.Length)).ToArray();
    }
}
