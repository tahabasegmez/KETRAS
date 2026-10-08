using System.Globalization;
using Ketras.Mavlink.Messages;
using Ketras.Station.Parameters;
using Ketras.Station.Vehicles;

namespace Ketras.Station.Setup;

/// <summary>
/// Actuator testing on the active vehicle with a safety gate: testing
/// is enabled only on an editable (connected, disarmed) vehicle after the operator confirms the propellers are removed;
/// motors are capped at <see cref="MotorCap"/>. Each driven output is kept alive by MAV_CMD_ACTUATOR_TEST with a 1 s
/// vehicle-side timeout, one command in flight at a time, so the vehicle releases everything within a second once the
/// station stops sending. A refusal, no answer, arming, link loss or another vehicle stops all and disables testing.
/// "Test all" runs each motor briefly at low thrust, then each control surface to +1, -1 and centre. Events are raised on
/// any thread.
/// </summary>
public sealed class ActuatorTestWorkflow : IDisposable
{
    /// <summary>The highest motor value the station sends.</summary>
    public const double MotorCap = 0.25;

    /// <summary>Vehicle-side timeout of each keep-alive command (param2).</summary>
    public const float KeepAliveSeconds = 1f;

    /// <summary>PX4 reads param5 ≥ 1000 as its own output function + 1000 (Commander's handleCommandActuatorTest).</summary>
    private const int Px4FunctionOffset = 1000;

    private const double SequenceMotor = 0.15;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SequenceMotorRun = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SequenceSurfaceHold = TimeSpan.FromMilliseconds(700);

    private readonly VehicleRegistry _vehicles;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<Output> _outputs = [];
    private readonly List<(TestActuator Actuator, double Value, TimeSpan Hold)> _sequence = [];
    private ITimer? _timer;
    private Vehicle? _vehicle;
    private int _cursor = -1;
    private bool _sending;
    private int _step = -1;
    private DateTimeOffset _stepStarted;

    /// <summary>Tests on the active vehicle of <paramref name="vehicles"/>.</summary>
    public ActuatorTestWorkflow(VehicleRegistry vehicles, TimeProvider time)
    {
        _vehicles = vehicles;
        _time = time;
        vehicles.ActiveChanged += OnActiveChanged;
    }

    /// <summary>Testing was enabled or disabled, an output changed, or the sequence moved.</summary>
    public event Action? Changed;

    /// <summary>The operator confirmed the propellers are removed and testing is on.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Why testing stopped by itself; empty otherwise.</summary>
    public string StopReason { get; private set; } = string.Empty;

    /// <summary>"Test all" runs.</summary>
    public bool IsSequenceRunning
    {
        get
        {
            lock (_lock)
            {
                return _step >= 0;
            }
        }
    }

    /// <summary>The running step, e.g. "Left Aileron +1"; empty when none.</summary>
    public string SequenceText
    {
        get
        {
            lock (_lock)
            {
                return _step < 0 ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $"{_sequence[_step].Actuator.Label} {_sequence[_step].Value:+0.##;-0.##;0}");
            }
        }
    }

    /// <summary>Enables testing after the operator confirmed the propellers are removed; false when the vehicle cannot be edited.</summary>
    public bool Enable()
    {
        var vehicle = _vehicles.Active;
        if (ParameterRules.WhyNotEditable(vehicle) != EditBlock.None)
        {
            return false;
        }

        lock (_lock)
        {
            _vehicle = vehicle;
            IsEnabled = true;
            StopReason = string.Empty;
            _timer ??= _time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Hands every output back to the vehicle and disables testing.</summary>
    public void Disable()
    {
        lock (_lock)
        {
            if (!IsEnabled)
            {
                return;
            }

            _step = -1;
            IsEnabled = false;
            foreach (var output in _outputs.Where(o => o.State == OutputState.Active))
            {
                output.State = OutputState.StopRequested;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Drives <paramref name="actuator"/> at <paramref name="value"/> (a motor at most <see cref="MotorCap"/>); ignored while disabled.</summary>
    public void Set(TestActuator actuator, double value)
    {
        lock (_lock)
        {
            if (!IsEnabled)
            {
                return;
            }

            var output = Find(actuator.Function) ?? Add(actuator.Function);
            output.Value = (float)Math.Clamp(value, actuator.Min, actuator.IsMotor ? Math.Min(actuator.Max, MotorCap) : actuator.Max);
            output.State = OutputState.Active;
        }

        Changed?.Invoke();
    }

    /// <summary>Hands <paramref name="function"/> back to the vehicle (a motor stops).</summary>
    public void Stop(int function)
    {
        lock (_lock)
        {
            if (Find(function) is { State: OutputState.Active } output)
            {
                output.State = OutputState.StopRequested;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The value <paramref name="function"/> is driven at; null when it is not.</summary>
    public double? ValueOf(int function)
    {
        lock (_lock)
        {
            return Find(function) is { State: OutputState.Active } output ? output.Value : null;
        }
    }

    /// <summary>Runs "Test all" over the motors and control surfaces of <paramref name="actuators"/>.</summary>
    public void StartSequence(IEnumerable<TestActuator> actuators)
    {
        lock (_lock)
        {
            if (!IsEnabled)
            {
                return;
            }

            _sequence.Clear();
            var list = actuators.ToList();
            _sequence.AddRange(list.Where(a => a.IsMotor).Select(a => (a, Math.Min(SequenceMotor, MotorCap), SequenceMotorRun)));
            foreach (var surface in list.Where(a => a.IsControlSurface))
            {
                _sequence.Add((surface, 1, SequenceSurfaceHold));
                _sequence.Add((surface, -1, SequenceSurfaceHold));
                _sequence.Add((surface, 0, SequenceSurfaceHold));
            }

            _step = -1;
            if (_sequence.Count > 0)
            {
                EnterStep(0);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Stops "Test all".</summary>
    public void StopSequence()
    {
        lock (_lock)
        {
            if (_step >= 0 && Find(_sequence[_step].Actuator.Function) is { State: OutputState.Active } output)
            {
                output.State = OutputState.StopRequested;
            }

            _step = -1;
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _vehicles.ActiveChanged -= OnActiveChanged;
        _timer?.Dispose();
    }

    private void OnActiveChanged(Vehicle? vehicle) => Fail("the active vehicle changed");

    private void EnterStep(int index)
    {
        _step = index;
        _stepStarted = _time.GetUtcNow();
        var (actuator, value, _) = _sequence[index];
        var output = Find(actuator.Function) ?? Add(actuator.Function);
        output.Value = (float)value;
        output.State = OutputState.Active;
    }

    private void Tick()
    {
        Output? next = null;
        float value = 0, timeout = 0;
        Vehicle? vehicle;
        var changed = false;
        lock (_lock)
        {
            vehicle = _vehicle;
            if (_outputs.Count == 0 && !IsEnabled)
            {
                return;
            }

            if (IsEnabled && ParameterRules.WhyNotEditable(vehicle) != EditBlock.None)
            {
                // Sending is not allowed any more: the vehicle releases the outputs by itself within the keep-alive timeout.
                _outputs.Clear();
                _step = -1;
                IsEnabled = false;
                StopReason = "the vehicle was armed or the link was lost";
                changed = true;
            }
            else
            {
                changed = AdvanceSequence();
                for (var i = 0; !_sending && i < _outputs.Count && next is null; i++)
                {
                    _cursor = (_cursor + 1) % _outputs.Count;
                    var output = _outputs[_cursor];
                    if (output.State == OutputState.Active)
                    {
                        (next, value, timeout) = (output, output.Value, KeepAliveSeconds);
                    }
                    else if (output.State == OutputState.StopRequested)
                    {
                        output.State = OutputState.Stopping;
                        (next, value, timeout) = (output, 0f, 0f);
                    }
                }

                _sending |= next is not null;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        if (next is not null && vehicle is not null)
        {
            _ = SendAsync(vehicle, next, value, timeout);
        }
    }

    private bool AdvanceSequence()
    {
        if (_step < 0 || _time.GetUtcNow() - _stepStarted < _sequence[_step].Hold)
        {
            return false;
        }

        var next = _step + 1;
        if ((next >= _sequence.Count || _sequence[next].Actuator.Function != _sequence[_step].Actuator.Function)
            && Find(_sequence[_step].Actuator.Function) is { State: OutputState.Active } output)
        {
            output.State = OutputState.StopRequested;
        }

        if (next >= _sequence.Count)
        {
            _step = -1;
        }
        else
        {
            EnterStep(next);
        }

        return true;
    }

    private async Task SendAsync(Vehicle vehicle, Output output, float value, float timeout)
    {
        var result = await vehicle.Session.Commands.SendLongAsync(MavCmd.ActuatorTest, value, timeout, 0, 0, Px4FunctionOffset + output.Function);
        lock (_lock)
        {
            _sending = false;
            if (result.IsAccepted)
            {
                if (output.State == OutputState.Stopping)
                {
                    _outputs.Remove(output);
                }

                return;
            }
        }

        Fail(result.Outcome == Mavlink.Protocols.CommandOutcome.NoResponse ? "the vehicle does not answer"
            : string.IsNullOrEmpty(result.Reason) ? $"refused ({result.Outcome})" : $"refused ({result.Reason})");
    }

    private void Fail(string reason)
    {
        lock (_lock)
        {
            if (!IsEnabled && _outputs.Count == 0)
            {
                return;
            }

            _outputs.Clear();
            _step = -1;
            IsEnabled = false;
            StopReason = reason;
        }

        Changed?.Invoke();
    }

    private Output? Find(int function) => _outputs.FirstOrDefault(o => o.Function == function);

    private Output Add(int function)
    {
        var output = new Output(function);
        _outputs.Add(output);
        return output;
    }

    private enum OutputState
    {
        Active,
        StopRequested,
        Stopping
    }

    private sealed class Output(int function)
    {
        public int Function { get; } = function;

        public float Value { get; set; }

        public OutputState State { get; set; }
    }
}
