using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Resources;
using Ketras.Settings;
using Ketras.Terrain;
using Ketras.Utilities;
using Ketras.Vehicles;

namespace Ketras.Telemetry;

/// <summary>How a readout is coloured.</summary>
public enum ReadoutState
{
    /// <summary>Normal text.</summary>
    Normal,

    /// <summary>Warning colour.</summary>
    Warning,

    /// <summary>Error colour.</summary>
    Error
}

/// <summary>
/// The active vehicle's telemetry for the panel at the bottom right of Planning and 3D: the attitude and heading for the
/// horizon, and text readouts for the three altitudes (above sea level, above home, above the terrain from the terrain
/// monitor), airspeed, ground speed, vertical speed and battery. The height above terrain is coloured by the terrain
/// monitor's clearance (Warning while a conflict is predicted, Error below the ground) and carries its message; the
/// airspeed is a warning outside the vehicle's normal range. "--" without a value. Expanded (remembered in the settings),
/// it adds the navigation (distance and time to the active item, altitude and cross-track error, the way home, time to
/// the end) and the power (voltage, current, charge used, time left, throttle, energy margin) from the vehicle and
/// <see cref="FlightMonitorWorker"/>.
/// </summary>
public sealed partial class TelemetryPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>The text of a missing value.</summary>
    public const string None = "--";

    /// <summary>Whether the panel shows the navigation and power block.</summary>
    public static SettingDefinition<bool> ExpandedSetting { get; } = new("telemetry.expanded", false);

    private readonly VehicleRegistry _vehicles;
    private readonly TerrainMonitorWorker _terrain;
    private readonly FlightMonitorWorker _flight;
    private readonly SettingsStore _settings;
    private readonly UnitService _units;
    private readonly IUiDispatcher _ui;
    private Vehicle? _vehicle;
    private VehicleLimits? _limits;

    /// <summary>Follows the active vehicle, the terrain monitor and the flight monitor.</summary>
    public TelemetryPanelViewModel(VehicleRegistry vehicles, TerrainMonitorWorker terrain, FlightMonitorWorker flight, SettingsStore settings,
        UnitService units, IUiDispatcher ui)
    {
        _vehicles = vehicles;
        _terrain = terrain;
        _flight = flight;
        _settings = settings;
        _units = units;
        _ui = ui;
        IsExpanded = settings.Get(ExpandedSetting);
        _vehicles.ActiveChanged += OnActiveChanged;
        _terrain.ClearanceChanged += OnClearance;
        _flight.Changed += OnFlight;
        Follow(vehicles.Active);
        ApplyClearance(terrain.Clearance);
        ApplyFlight(flight.Status);
    }

    /// <summary>The navigation and power block is shown.</summary>
    [ObservableProperty] public partial bool IsExpanded { get; private set; }

    /// <summary>Distance to the active mission item.</summary>
    [ObservableProperty] public partial string Waypoint { get; private set; } = None;

    /// <summary>Time to the active item.</summary>
    [ObservableProperty] public partial string WaypointEta { get; private set; } = None;

    /// <summary>Altitude error, signed: + below the target.</summary>
    [ObservableProperty] public partial string AltitudeError { get; private set; } = None;

    /// <summary>The altitude error is past <see cref="FlightMonitorWorker.AltitudeErrorMeters"/>.</summary>
    [ObservableProperty] public partial bool IsAltitudeErrorWarning { get; private set; }

    /// <summary>Cross-track error.</summary>
    [ObservableProperty] public partial string CrossTrack { get; private set; } = None;

    /// <summary>The cross-track error is past twice the loiter radius.</summary>
    [ObservableProperty] public partial bool IsCrossTrackWarning { get; private set; }

    /// <summary>Distance and bearing to the home.</summary>
    [ObservableProperty] public partial string Home { get; private set; } = None;

    /// <summary>Time to the end of the mission (or home).</summary>
    [ObservableProperty] public partial string EndEta { get; private set; } = None;

    /// <summary>Pack voltage.</summary>
    [ObservableProperty] public partial string Voltage { get; private set; } = None;

    /// <summary>The voltage's tooltip: per cell when the cell count is known.</summary>
    [ObservableProperty] public partial string VoltageDetail { get; private set; } = Strings.TelemetryVoltageHelp;

    /// <summary>Current draw.</summary>
    [ObservableProperty] public partial string Current { get; private set; } = None;

    /// <summary>Charge used.</summary>
    [ObservableProperty] public partial string Consumed { get; private set; } = None;

    /// <summary>Time the battery lasts: the autopilot's estimate, else the budget's.</summary>
    [ObservableProperty] public partial string TimeLeft { get; private set; } = None;

    /// <summary>Throttle.</summary>
    [ObservableProperty] public partial string Throttle { get; private set; } = None;

    /// <summary>Energy left at the end above the reserve, %.</summary>
    [ObservableProperty] public partial string Margin { get; private set; } = None;

    /// <summary>How the margin is coloured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarginWarning), nameof(IsMarginError))]
    public partial ReadoutState MarginState { get; private set; }

    /// <summary>The margin is a warning.</summary>
    public bool IsMarginWarning => MarginState == ReadoutState.Warning;

    /// <summary>The margin is an error.</summary>
    public bool IsMarginError => MarginState == ReadoutState.Error;

    /// <summary>The margin's tooltip: the budget behind it.</summary>
    [ObservableProperty] public partial string MarginDetail { get; private set; } = Strings.TelemetryMarginHelp;

    /// <summary>A duration as "m:ss", or "h:mm:ss" from an hour.</summary>
    public static string Duration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds)));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    /// <summary>Roll, degrees.</summary>
    [ObservableProperty] public partial double? Roll { get; private set; }

    /// <summary>Pitch, degrees.</summary>
    [ObservableProperty] public partial double? Pitch { get; private set; }

    /// <summary>Heading, degrees.</summary>
    [ObservableProperty] public partial double? Heading { get; private set; }

    /// <summary>Altitude above mean sea level.</summary>
    [ObservableProperty] public partial string Amsl { get; private set; } = None;

    /// <summary>Altitude above home.</summary>
    [ObservableProperty] public partial string Relative { get; private set; } = None;

    /// <summary>Height above the terrain.</summary>
    [ObservableProperty] public partial string Agl { get; private set; } = None;

    /// <summary>How the height above terrain is coloured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAglWarning), nameof(IsAglError))]
    public partial ReadoutState AglState { get; private set; }

    /// <summary>The height above terrain is a warning.</summary>
    public bool IsAglWarning => AglState == ReadoutState.Warning;

    /// <summary>The height above terrain is an error.</summary>
    public bool IsAglError => AglState == ReadoutState.Error;

    /// <summary>The height above terrain's tooltip: what it is, then the terrain monitor's message.</summary>
    [ObservableProperty] public partial string AglDetail { get; private set; } = Strings.TelemetryAglHelp;

    /// <summary>Airspeed.</summary>
    [ObservableProperty] public partial string Airspeed { get; private set; } = None;

    /// <summary>The airspeed is outside the vehicle's normal range.</summary>
    [ObservableProperty] public partial bool IsAirspeedWarning { get; private set; }

    /// <summary>Ground speed.</summary>
    [ObservableProperty] public partial string GroundSpeed { get; private set; } = None;

    /// <summary>Vertical speed, signed.</summary>
    [ObservableProperty] public partial string Climb { get; private set; } = None;

    /// <summary>Remaining battery.</summary>
    [ObservableProperty] public partial string Battery { get; private set; } = None;

    /// <inheritdoc />
    public void Dispose()
    {
        _vehicles.ActiveChanged -= OnActiveChanged;
        _terrain.ClearanceChanged -= OnClearance;
        _flight.Changed -= OnFlight;
        Follow(null);
    }

    /// <summary>Takes a snapshot's values; call on the UI thread.</summary>
    internal void Apply(VehicleSnapshot? snapshot)
    {
        Roll = snapshot?.RollDegrees;
        Pitch = snapshot?.PitchDegrees;
        Heading = snapshot?.HeadingDegrees;
        Amsl = snapshot?.AltitudeAmslMeters is { } amsl ? _units.Altitude(amsl) : None;
        Relative = snapshot?.RelativeAltitudeMeters is { } relative ? _units.Altitude(relative) : None;
        Airspeed = snapshot?.AirspeedMps is { } airspeed ? _units.Speed(airspeed) : None;
        IsAirspeedWarning = snapshot?.AirspeedMps is { } speed && _limits is { } limits && snapshot.IsArmed
                            && (speed < limits.MinAirspeedMps || speed > limits.MaxAirspeedMps);
        GroundSpeed = snapshot?.GroundSpeedMps is { } ground ? _units.Speed(ground) : None;
        Climb = snapshot?.ClimbRateMps is { } climb ? (climb > 0.05 ? "+" : string.Empty) + _units.Speed(climb) : None;
        Battery = snapshot?.BatteryPercent is { } battery ? string.Create(CultureInfo.CurrentCulture, $"{battery} %") : None;
        if (snapshot is null)
        {
            ApplyClearance(null);
        }

        var nav = snapshot?.Navigation;
        var executing = snapshot is { IsArmed: true } && nav is not null;
        Waypoint = executing && nav is not null ? _units.Distance(nav.WaypointDistanceMeters) : None;
        AltitudeError = executing && nav is not null ? (nav.AltitudeErrorMeters > 0.5 ? "+" : string.Empty) + _units.Altitude(nav.AltitudeErrorMeters) : None;
        IsAltitudeErrorWarning = executing && nav is not null && Math.Abs(nav.AltitudeErrorMeters) > FlightMonitorWorker.AltitudeErrorMeters;
        CrossTrack = executing && nav is not null ? _units.Distance(Math.Abs(nav.CrossTrackErrorMeters)) : None;
        IsCrossTrackWarning = executing && nav is not null && _limits is { } l && Math.Abs(nav.CrossTrackErrorMeters) > 2 * l.DefaultLoiterRadiusMeters;
        var power = snapshot?.Battery;
        Voltage = power?.VoltageVolts is { } volts ? string.Create(CultureInfo.CurrentCulture, $"{volts:F1} V") : None;
        VoltageDetail = power is { VoltageVolts: { } v, CellCount: > 1 and var cells }
            ? string.Format(CultureInfo.CurrentCulture, Strings.TelemetryVoltagePerCell, v / cells, cells)
            : Strings.TelemetryVoltageHelp;
        Current = power?.CurrentAmps is { } amps ? string.Create(CultureInfo.CurrentCulture, $"{amps:F1} A") : None;
        Consumed = power?.ConsumedMah is { } mah ? string.Create(CultureInfo.CurrentCulture, $"{mah:F0} mAh") : None;
        Throttle = snapshot?.ThrottlePercent is { } throttle ? string.Create(CultureInfo.CurrentCulture, $"{throttle:F0} %") : None;
        TimeLeft = power?.TimeRemainingSeconds is { } left ? Duration(left) : TimeLeft;
    }

    /// <summary>Takes the flight monitor's reading; call on the UI thread.</summary>
    internal void ApplyFlight(FlightStatus status)
    {
        WaypointEta = status.NextItemSeconds is { } next ? Duration(next) : None;
        EndEta = status.EndSeconds is { } end ? Duration(end) : None;
        Home = status.HomeDistanceMeters is { } distance && status.HomeBearingDegrees is { } bearing
            ? string.Create(CultureInfo.CurrentCulture, $"{_units.Distance(distance)} {bearing:000}°")
            : None;
        if (_vehicle?.Snapshot.Battery?.TimeRemainingSeconds is null)
        {
            TimeLeft = status.Energy is { EnduranceSeconds: var endurance } && double.IsFinite(endurance) ? Duration(endurance) : None;
        }

        if (status.Energy is not { } energy)
        {
            Margin = None;
            MarginState = ReadoutState.Normal;
            MarginDetail = Strings.TelemetryMarginHelp;
            return;
        }

        Margin = string.Create(CultureInfo.CurrentCulture, $"{energy.MarginPercent:F0} %");
        MarginState = energy.MarginMah < 0 ? ReadoutState.Error
            : energy.MarginPercent < EnergyCalculator.WarningMarginPercent ? ReadoutState.Warning
            : ReadoutState.Normal;
        MarginDetail = string.Format(CultureInfo.CurrentCulture, Strings.TelemetryMarginDetail, energy.NeededMah, energy.UsableMah, energy.CurrentAmps,
            energy.Source == CurrentSource.Measured ? Strings.TelemetryCurrentMeasured : Strings.TelemetryCurrentEntered);
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _settings.Set(ExpandedSetting, IsExpanded);
    }

    private void OnFlight(FlightStatus status) => _ui.Post(() => ApplyFlight(status));

    /// <summary>Takes the terrain monitor's clearance; call on the UI thread.</summary>
    internal void ApplyClearance(TerrainClearance? clearance)
    {
        Agl = _vehicle is not null && clearance?.AboveGroundMeters is { } agl ? _units.Altitude(agl) : None;
        AglState = _vehicle is null || clearance is null ? ReadoutState.Normal
            : clearance.AboveGroundMeters < 0 ? ReadoutState.Error
            : clearance.IsWarning ? ReadoutState.Warning
            : ReadoutState.Normal;
        AglDetail = _vehicle is not null && clearance?.Message is { Length: > 0 } message ? $"{Strings.TelemetryAglHelp}: {message}" : Strings.TelemetryAglHelp;
    }

    private void OnActiveChanged(Vehicle? vehicle) => _ui.Post(() => Follow(vehicle));

    private void OnClearance(TerrainClearance clearance) => _ui.Post(() => ApplyClearance(clearance));

    private void Follow(Vehicle? vehicle)
    {
        if (_vehicle is not null)
        {
            _vehicle.SnapshotChanged -= OnSnapshot;
            _vehicle.LimitsChanged -= OnLimits;
        }

        _vehicle = vehicle;
        _limits = vehicle?.Limits;
        if (vehicle is not null)
        {
            vehicle.SnapshotChanged += OnSnapshot;
            vehicle.LimitsChanged += OnLimits;
        }

        Apply(vehicle?.Snapshot);
    }

    private void OnSnapshot(VehicleSnapshot snapshot) => _ui.Post(() => Apply(snapshot));

    private void OnLimits(VehicleLimits limits) => _ui.Post(() => _limits = limits);
}
