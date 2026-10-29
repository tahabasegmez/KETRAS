using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;
using Ketras.Parameters;
using Ketras.Vehicles;

namespace Ketras.Setup;

/// <summary>A sensor calibration PX4 runs.</summary>
public enum CalibrationKind
{
    /// <summary>Gyroscope (keep still).</summary>
    Gyro,

    /// <summary>Accelerometer (six sides).</summary>
    Accelerometer,

    /// <summary>Level horizon (keep in cruise attitude).</summary>
    Level,

    /// <summary>Compass (rotate on each side asked).</summary>
    Compass,

    /// <summary>Airspeed (shield, then blow into the pitot).</summary>
    Airspeed
}

/// <summary>Where a calibration is.</summary>
public enum CalibrationPhase
{
    /// <summary>None started.</summary>
    Idle,

    /// <summary>The command is sent; waiting for the vehicle to start.</summary>
    Starting,

    /// <summary>The vehicle is calibrating.</summary>
    Running,

    /// <summary>Cancel is sent.</summary>
    Cancelling,

    /// <summary>Finished.</summary>
    Done,

    /// <summary>Failed or refused.</summary>
    Failed,

    /// <summary>Cancelled; the previous calibration is kept.</summary>
    Cancelled
}

/// <summary>A side of the vehicle during a calibration.</summary>
public enum SideState
{
    /// <summary>Not done.</summary>
    Incomplete,

    /// <summary>Detected; hold still.</summary>
    InProgress,

    /// <summary>Done.</summary>
    Completed
}

/// <summary>
/// One sensor calibration at a time on the active vehicle: sends MAV_CMD_PREFLIGHT_CALIBRATION,
/// follows PX4's <c>[cal]</c> status texts (progress, sides, done / failed / cancelled) and, when it ends, reads the
/// calibration parameters again. Starts only while the vehicle can be edited. Events are raised on any thread.
/// </summary>
public sealed class CalibrationWorkflow : IDisposable
{
    private readonly VehicleRegistry _vehicles;
    private readonly object _lock = new();
    private readonly Dictionary<string, SideState> _sides = [];
    private readonly List<string> _lines = [];
    private Vehicle? _vehicle;

    /// <summary>Calibrates the active vehicle of <paramref name="vehicles"/>.</summary>
    public CalibrationWorkflow(VehicleRegistry vehicles)
    {
        _vehicles = vehicles;
    }

    /// <summary>The state changed.</summary>
    public event Action? Changed;

    /// <summary>The calibration run or last run.</summary>
    public CalibrationKind? Kind { get; private set; }

    /// <summary>Where it is.</summary>
    public CalibrationPhase Phase { get; private set; }

    /// <summary>Progress, 0 to 100.</summary>
    public int Percent { get; private set; }

    /// <summary>The result line: the reason it failed, or empty.</summary>
    public string Outcome { get; private set; } = string.Empty;

    /// <summary>It is under way.</summary>
    public bool IsBusy => Phase is CalibrationPhase.Starting or CalibrationPhase.Running or CalibrationPhase.Cancelling;

    /// <summary>The sides asked for, in display order.</summary>
    public IReadOnlyList<(string Side, SideState State)> Sides
    {
        get
        {
            lock (_lock)
            {
                return [.. CalibrationTextParser.Sides.Where(_sides.ContainsKey).Select(s => (s, _sides[s]))];
            }
        }
    }

    /// <summary>The vehicle's calibration texts so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Starts a calibration; refused while one runs or the vehicle cannot be edited.</summary>
    public async Task<bool> StartAsync(CalibrationKind kind)
    {
        var vehicle = _vehicles.Active;
        lock (_lock)
        {
            if (IsBusy || ParameterRules.WhyNotEditable(vehicle) != EditBlock.None)
            {
                return false;
            }

            Follow(vehicle);
            Kind = kind;
            Phase = CalibrationPhase.Starting;
            Percent = 0;
            Outcome = string.Empty;
            _sides.Clear();
            _lines.Clear();
        }

        Changed?.Invoke();
        var (p1, p2, p5, p6) = kind switch
        {
            CalibrationKind.Gyro => (1f, 0f, 0f, 0f),
            CalibrationKind.Compass => (0f, 1f, 0f, 0f),
            CalibrationKind.Accelerometer => (0f, 0f, 1f, 0f),
            CalibrationKind.Level => (0f, 0f, 2f, 0f),
            _ => (0f, 0f, 0f, 2f)
        };
        var result = await vehicle!.Session.Commands.SendLongAsync(MavCmd.PreflightCalibration, p1, p2, 0, 0, p5, p6);
        if (!result.IsAccepted)
        {
            Finish(CalibrationPhase.Failed, result.Outcome == CommandOutcome.NoResponse ? "no answer from the vehicle"
                : string.IsNullOrEmpty(result.Reason) ? result.Outcome.ToString() : result.Reason, reread: false);
            return false;
        }

        return true;
    }

    /// <summary>Asks the vehicle to cancel (MAV_CMD_PREFLIGHT_CALIBRATION with every param 0).</summary>
    public async Task CancelAsync()
    {
        Vehicle? vehicle;
        lock (_lock)
        {
            if (Phase is not (CalibrationPhase.Starting or CalibrationPhase.Running))
            {
                return;
            }

            Phase = CalibrationPhase.Cancelling;
            vehicle = _vehicle;
        }

        Changed?.Invoke();
        if (vehicle is not null)
        {
            await vehicle.Session.Commands.SendLongAsync(MavCmd.PreflightCalibration);
        }
    }

    /// <summary>Clears a finished calibration.</summary>
    public void Close()
    {
        lock (_lock)
        {
            if (IsBusy)
            {
                return;
            }

            Phase = CalibrationPhase.Idle;
            Kind = null;
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose() => Follow(null);

    private void Follow(Vehicle? vehicle)
    {
        if (ReferenceEquals(vehicle, _vehicle))
        {
            return;
        }

        if (_vehicle is not null)
        {
            _vehicle.Session.Status.StatusText -= OnStatusText;
        }

        _vehicle = vehicle;
        if (vehicle is not null)
        {
            vehicle.Session.Status.StatusText += OnStatusText;
        }
    }

    private void OnStatusText(MavSeverity severity, string text)
    {
        if (CalibrationTextParser.Parse(text) is not { } calibration)
        {
            return;
        }

        lock (_lock)
        {
            if (!IsBusy)
            {
                return;
            }

            if (calibration.Kind != CalibrationEventKind.Progress)
            {
                _lines.Add(calibration.Text);
            }

            switch (calibration.Kind)
            {
                case CalibrationEventKind.Started:
                    Phase = Phase == CalibrationPhase.Cancelling ? Phase : CalibrationPhase.Running;
                    _sides.Clear();
                    foreach (var side in SidesFor(calibration.Sensor))
                    {
                        _sides[side] = SideState.Incomplete;
                    }

                    break;
                case CalibrationEventKind.Progress:
                    Percent = calibration.Percent ?? Percent;
                    break;
                case CalibrationEventKind.OrientationDetected when calibration.Side is { } side && _sides.ContainsKey(side):
                    _sides[side] = SideState.InProgress;
                    break;
                case CalibrationEventKind.SideDone or CalibrationEventKind.SideAlreadyDone when calibration.Side is { } side && _sides.ContainsKey(side):
                    _sides[side] = SideState.Completed;
                    break;
            }
        }

        switch (calibration.Kind)
        {
            case CalibrationEventKind.Done:
                lock (_lock)
                {
                    Percent = 100;
                    foreach (var side in _sides.Keys.ToList())
                    {
                        _sides[side] = SideState.Completed;
                    }
                }

                Finish(CalibrationPhase.Done, string.Empty);
                break;
            case CalibrationEventKind.Cancelled:
                Finish(CalibrationPhase.Cancelled, string.Empty);
                break;
            case CalibrationEventKind.Failed:
                Finish(CalibrationPhase.Failed, calibration.Text);
                break;
            default:
                Changed?.Invoke();
                break;
        }
    }

    private IEnumerable<string> SidesFor(string? sensor) => sensor switch
    {
        "accel" => CalibrationTextParser.Sides,
        "mag" => CalibrationTextParser.CompassSides(_vehicle?.Parameters["SENS_MAG_SIDES"] ?? _vehicle?.Parameters["CAL_MAG_SIDES"]),
        "gyro" => ["down"],
        _ => []
    };

    private void Finish(CalibrationPhase phase, string outcome, bool reread = true)
    {
        Vehicle? vehicle;
        lock (_lock)
        {
            Phase = phase;
            Outcome = outcome;
            vehicle = _vehicle;
        }

        Changed?.Invoke();
        if (reread && vehicle is not null)
        {
            _ = RereadAsync(vehicle);
        }
    }

    /// <summary>The calibration wrote CAL_* and SENS_* on the vehicle; reading them keeps the station's values current.</summary>
    private static async Task RereadAsync(Vehicle vehicle)
    {
        foreach (var name in vehicle.Parameters.Values.Keys.Where(n => n.StartsWith("CAL_", StringComparison.Ordinal) || n.StartsWith("SENS_", StringComparison.Ordinal)).ToList())
        {
            await vehicle.Parameters.ReadAsync(name);
        }
    }
}
