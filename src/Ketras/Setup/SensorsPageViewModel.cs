using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Parameters;
using Ketras.Resources;
using Ketras.Utilities;

namespace Ketras.Setup;

/// <summary>A sensor the vehicle can calibrate.</summary>
public sealed partial class SensorRowViewModel(CalibrationKind kind, string name, string icon, string help, bool asksOrientation,
    Func<ParameterReader, bool> visible, Func<ParameterReader, bool?> required, Action<SensorRowViewModel> calibrate) : ObservableObject
{
    /// <summary>The calibration.</summary>
    public CalibrationKind Kind { get; } = kind;

    /// <summary>Its name.</summary>
    public string Name { get; } = name;

    /// <summary>Its icon.</summary>
    public string Icon { get; } = icon;

    /// <summary>What the operator does during it.</summary>
    public string Help { get; } = help;

    /// <summary>The autopilot orientation is checked before it starts.</summary>
    public bool AsksOrientation { get; } = asksOrientation;

    /// <summary>The vehicle has this sensor.</summary>
    [ObservableProperty] public partial bool IsVisible { get; private set; }

    /// <summary>"Calibrated", "Not calibrated", or empty when the state is not known (level horizon).</summary>
    [ObservableProperty] public partial string StateText { get; private set; } = string.Empty;

    /// <summary>It needs calibrating.</summary>
    [ObservableProperty] public partial bool IsRequired { get; private set; }

    /// <summary>It is calibrated.</summary>
    [ObservableProperty] public partial bool IsReady { get; private set; }

    /// <summary>Calibrate can be pressed.</summary>
    [ObservableProperty] public partial bool CanCalibrate { get; private set; }

    /// <summary>Re-evaluates the row.</summary>
    public void Refresh(ParameterReader reader, bool hasValues, bool canStart)
    {
        IsVisible = hasValues && visible(reader);
        var state = hasValues ? required(reader) : null;
        IsRequired = state == true;
        IsReady = state == false;
        StateText = state switch { true => Strings.SensorsNotCalibrated, false => Strings.SensorsCalibrated, _ => string.Empty };
        CanCalibrate = canStart;
    }

    [RelayCommand]
    private void Calibrate() => calibrate(this);
}

/// <summary>A side tile of a running calibration.</summary>
/// <param name="Label">"Nose down".</param>
/// <param name="State">Its state.</param>
public sealed record SideTileViewModel(string Label, SideState State)
{
    /// <summary>"hold still", "completed" or "incomplete".</summary>
    public string StateText => State switch
    {
        SideState.Completed => Strings.SensorsSideCompleted,
        SideState.InProgress => Strings.SensorsSideHold,
        _ => Strings.SensorsSideIncomplete
    };

    /// <summary>Done.</summary>
    public bool IsCompleted => State == SideState.Completed;

    /// <summary>Being held.</summary>
    public bool IsInProgress => State == SideState.InProgress;
}

/// <summary>
/// The Sensors page: each sensor the vehicle has with its calibration state and Calibrate (which first shows what to do
/// and, for the accelerometer and compass, the autopilot orientation), the running calibration's card (progress, sides,
/// the vehicle's lines, Cancel), and the autopilot and external compass orientations.
/// </summary>
public sealed partial class SensorsPageViewModel : ObservableObject, IDisposable
{
    private static readonly Dictionary<string, string> SideLabels = new()
    {
        ["down"] = Strings.SensorsSideDown,
        ["up"] = Strings.SensorsSideUp,
        ["left"] = Strings.SensorsSideLeft,
        ["right"] = Strings.SensorsSideRight,
        ["front"] = Strings.SensorsSideFront,
        ["back"] = Strings.SensorsSideBack
    };

    private readonly SetupContext _context;
    private readonly CalibrationWorkflow _calibration;
    private readonly VehicleSetupService _setup;
    private readonly IUiDispatcher _ui;
    private readonly Action<string, Notifications.NotificationSeverity> _log;
    private SensorRowViewModel? _active;
    private CalibrationPhase _lastPhase;

    /// <summary>Follows <paramref name="context"/> and <paramref name="calibration"/>.</summary>
    public SensorsPageViewModel(SetupContext context, CalibrationWorkflow calibration, VehicleSetupService setup, IUiDispatcher ui,
        Action<string, Notifications.NotificationSeverity> log)
    {
        _context = context;
        _calibration = calibration;
        _setup = setup;
        _ui = ui;
        _log = log;
        Sensors =
        [
            new(CalibrationKind.Compass, Strings.SensorsCompass, "Compass", Strings.SensorsCompassHelp, true, SetupRules.HasCompass, r => SetupRules.CompassRequired(r), Ask),
            new(CalibrationKind.Gyro, Strings.SensorsGyro, "Rotate3d", Strings.SensorsGyroHelp, false, _ => true, r => SetupRules.GyroRequired(r), Ask),
            new(CalibrationKind.Accelerometer, Strings.SensorsAccelerometer, "Move3d", Strings.SensorsAccelerometerHelp, true, _ => true, r => SetupRules.AccelerometerRequired(r), Ask),
            new(CalibrationKind.Level, Strings.SensorsLevel, "Plane", Strings.SensorsLevelHelp, false, _ => true, _ => null, Ask),
            new(CalibrationKind.Airspeed, Strings.SensorsAirspeed, "Wind", Strings.SensorsAirspeedHelp, false, SetupRules.HasAirspeed, r => SetupRules.AirspeedRequired(r), Ask)
        ];
        BoardOrientation = new ParameterFieldViewModel("SENS_BOARD_ROT", context, Strings.SensorsBoardOrientation);
        Orientation = new ParameterSectionViewModel(Strings.SensorsOrientation,
        [
            BoardOrientation,
            .. Enumerable.Range(0, 4).Select(i => new ParameterFieldViewModel(string.Create(CultureInfo.InvariantCulture, $"CAL_MAG{i}_ROT"), context,
                string.Format(CultureInfo.CurrentCulture, Strings.SensorsExternalCompass, i)))
        ]);
        context.ValuesChanged += _ => Refresh();
        context.StateChanged += Refresh;
        calibration.Changed += OnCalibration;
        Refresh();
    }

    /// <summary>The page title.</summary>
    public string Title => Strings.SetupPageSensors;

    /// <summary>The sensors.</summary>
    public IReadOnlyList<SensorRowViewModel> Sensors { get; }

    /// <summary>The orientations.</summary>
    public ParameterSectionViewModel Orientation { get; }

    /// <summary>The autopilot orientation, also shown before an accelerometer or compass calibration.</summary>
    public ParameterFieldViewModel BoardOrientation { get; }

    /// <summary>A calibration waits for Start.</summary>
    [ObservableProperty] public partial bool IsAsking { get; private set; }

    /// <summary>The card shows a calibration (running or finished).</summary>
    [ObservableProperty] public partial bool IsSessionVisible { get; private set; }

    /// <summary>"Calibrate accelerometer".</summary>
    [ObservableProperty] public partial string SessionTitle { get; private set; } = string.Empty;

    /// <summary>What to do now, or the result.</summary>
    [ObservableProperty] public partial string SessionText { get; private set; } = string.Empty;

    /// <summary>The result is a success.</summary>
    [ObservableProperty] public partial bool IsSucceeded { get; private set; }

    /// <summary>The result is a failure.</summary>
    [ObservableProperty] public partial bool IsFailed { get; private set; }

    /// <summary>The asking card shows the autopilot orientation.</summary>
    [ObservableProperty] public partial bool ShowsOrientation { get; private set; }

    /// <summary>Progress, 0 to 100.</summary>
    [ObservableProperty] public partial double Percent { get; private set; }

    /// <summary>The progress bar shows.</summary>
    [ObservableProperty] public partial bool ShowsProgress { get; private set; }

    /// <summary>The sides asked for.</summary>
    public ObservableCollection<SideTileViewModel> Sides { get; } = [];

    /// <summary>The vehicle's last calibration lines.</summary>
    [ObservableProperty] public partial string Lines { get; private set; } = string.Empty;

    /// <summary>Cancel shows.</summary>
    [ObservableProperty] public partial bool CanCancel { get; private set; }

    /// <summary>Close shows.</summary>
    [ObservableProperty] public partial bool CanClose { get; private set; }

    /// <summary>Reboot shows (after a compass calibration).</summary>
    [ObservableProperty] public partial bool CanReboot { get; private set; }

    /// <inheritdoc />
    public void Dispose() => _calibration.Changed -= OnCalibration;

    /// <summary>Re-evaluates the page.</summary>
    public void Refresh()
    {
        var hasValues = _context.Has("CAL_GYRO0_ID");
        foreach (var sensor in Sensors)
        {
            sensor.Refresh(_context.Reader, hasValues, _context.CanEdit && !_calibration.IsBusy && !IsAsking);
        }

        Orientation.Refresh(field => !field.Name.StartsWith("CAL_MAG", StringComparison.Ordinal)
            || (_context.Value(field.Name.Replace("_ROT", "_ID", StringComparison.Ordinal)) is > 0 && _context.Value(field.Name) is >= 0));
        UpdateSession();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        IsAsking = false;
        if (_active is not null && !await _calibration.StartAsync(_active.Kind))
        {
            _log(string.Format(CultureInfo.CurrentCulture, Strings.SensorsNotStarted, _active.Name, _calibration.Outcome), Notifications.NotificationSeverity.Error);
        }

        Refresh();
    }

    [RelayCommand]
    private void CancelAsk()
    {
        IsAsking = false;
        _active = null;
        Refresh();
    }

    [RelayCommand]
    private Task CancelCalibrationAsync() => _calibration.CancelAsync();

    [RelayCommand]
    private void CloseSession()
    {
        _calibration.Close();
        _active = null;
        Refresh();
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        var result = await _setup.RebootAsync();
        _log(result.IsAccepted ? string.Format(CultureInfo.CurrentCulture, Strings.SetupActionDone, Strings.ParamsReboot)
            : string.Format(CultureInfo.CurrentCulture, Strings.SetupActionRefused, Strings.ParamsReboot, result.Message),
            result.IsAccepted ? Notifications.NotificationSeverity.Success : Notifications.NotificationSeverity.Error);
    }

    private void Ask(SensorRowViewModel sensor)
    {
        _active = sensor;
        IsAsking = true;
        Refresh();
    }

    private void OnCalibration() => _ui.Post(() =>
    {
        var phase = _calibration.Phase;
        if (phase != _lastPhase && _calibration.Kind is { } kind)
        {
            var name = Sensors.First(s => s.Kind == kind).Name;
            switch (phase)
            {
                case CalibrationPhase.Done:
                    _log(string.Format(CultureInfo.CurrentCulture, Strings.SensorsLogDone, name), Notifications.NotificationSeverity.Success);
                    break;
                case CalibrationPhase.Failed:
                    _log(string.Format(CultureInfo.CurrentCulture, Strings.SensorsLogFailed, name, _calibration.Outcome), Notifications.NotificationSeverity.Error);
                    break;
                case CalibrationPhase.Cancelled:
                    _log(string.Format(CultureInfo.CurrentCulture, Strings.SensorsLogCancelled, name), Notifications.NotificationSeverity.Warning);
                    break;
            }
        }

        _lastPhase = phase;
        Refresh();
    });

    private void UpdateSession()
    {
        var phase = _calibration.Phase;
        var sensor = _calibration.Kind is { } kind ? Sensors.First(s => s.Kind == kind) : _active;
        IsSessionVisible = IsAsking || phase != CalibrationPhase.Idle;
        ShowsOrientation = IsAsking && _active?.AsksOrientation == true && BoardOrientation.Exists;
        SessionTitle = sensor is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Strings.SensorsCalibrateTitle, sensor.Name);
        SessionText = IsAsking ? _active?.Help ?? string.Empty : phase switch
        {
            CalibrationPhase.Starting => Strings.SensorsStarting,
            CalibrationPhase.Running => sensor?.Help ?? string.Empty,
            CalibrationPhase.Cancelling => Strings.SensorsCancelling,
            CalibrationPhase.Done => Strings.SensorsDone,
            CalibrationPhase.Cancelled => Strings.SensorsCancelled,
            CalibrationPhase.Failed => string.Format(CultureInfo.CurrentCulture, Strings.SensorsFailed, _calibration.Outcome),
            _ => string.Empty
        };
        IsSucceeded = !IsAsking && phase == CalibrationPhase.Done;
        IsFailed = !IsAsking && phase == CalibrationPhase.Failed;
        ShowsProgress = !IsAsking && (_calibration.IsBusy || phase == CalibrationPhase.Done);
        Percent = _calibration.Percent;
        Sides.Clear();
        if (!IsAsking)
        {
            foreach (var (side, state) in _calibration.Sides)
            {
                Sides.Add(new SideTileViewModel(SideLabels.GetValueOrDefault(side, side), state));
            }
        }

        Lines = IsAsking ? string.Empty : string.Join("\n", _calibration.Lines.TakeLast(6));
        CanCancel = !IsAsking && phase is CalibrationPhase.Starting or CalibrationPhase.Running;
        CanClose = !IsAsking && phase is CalibrationPhase.Done or CalibrationPhase.Failed or CalibrationPhase.Cancelled;
        CanReboot = CanClose && phase == CalibrationPhase.Done && _calibration.Kind == CalibrationKind.Compass && _context.CanEdit;
    }
}
