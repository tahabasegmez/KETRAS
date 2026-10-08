using System.Globalization;
using Ketras.Mavlink.Autopilots;
using Ketras.Station.Parameters;
using Ketras.Station.Vehicles;

namespace Ketras.Station.Setup;

/// <summary>
/// The RC transmitter calibration on the active vehicle: feeds the live RC_CHANNELS into <see cref="RcCalibrationWizard"/>
/// and, when the operator finishes, writes the result.
/// Also keeps the latest RC values for the channel monitor. Events are raised on any thread.
/// </summary>
public sealed class RadioCalibrationWorkflow : IDisposable
{
    /// <summary>The stick mapping parameters.</summary>
    public static IReadOnlyList<(StickFunction Function, string Parameter)> StickParameters { get; } =
    [
        (StickFunction.Roll, "RC_MAP_ROLL"),
        (StickFunction.Pitch, "RC_MAP_PITCH"),
        (StickFunction.Yaw, "RC_MAP_YAW"),
        (StickFunction.Throttle, "RC_MAP_THROTTLE")
    ];

    private readonly VehicleRegistry _vehicles;
    private readonly TimeProvider _time;
    private readonly RcCalibrationWizard _wizard = new();
    private readonly object _lock = new();
    private Vehicle? _vehicle;
    private IReadOnlyList<ushort> _channels = [];

    /// <summary>Follows the active vehicle of <paramref name="vehicles"/>.</summary>
    public RadioCalibrationWorkflow(VehicleRegistry vehicles, TimeProvider time)
    {
        _vehicles = vehicles;
        _time = time;
        vehicles.ActiveChanged += Follow;
        Follow(vehicles.Active);
    }

    /// <summary>New RC values arrived.</summary>
    public event Action? ChannelsChanged;

    /// <summary>The wizard's step changed, or it started or ended.</summary>
    public event Action? StepChanged;

    /// <summary>The latest RC values (PWM, µs).</summary>
    public IReadOnlyList<ushort> Channels
    {
        get
        {
            lock (_lock)
            {
                return _channels;
            }
        }
    }

    /// <summary>The current step; null when no calibration runs.</summary>
    public WizardStep? Step
    {
        get
        {
            lock (_lock)
            {
                return _wizard.Step;
            }
        }
    }

    /// <summary>The step waits for Next (neutral, switches, complete).</summary>
    public bool CanNext
    {
        get
        {
            lock (_lock)
            {
                return _wizard.NextEnabled;
            }
        }
    }

    /// <summary>Starts; returns why not when it cannot (empty when started).</summary>
    public string Start()
    {
        lock (_lock)
        {
            if (ParameterRules.WhyNotEditable(_vehicles.Active) != EditBlock.None)
            {
                return "the vehicle cannot be edited now";
            }

            if (_wizard.ChannelCount < RcCalibrationWizard.MinChannels)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} RC channels received; at least {1} are needed", _wizard.ChannelCount, RcCalibrationWizard.MinChannels);
            }

            _wizard.Start();
        }

        StepChanged?.Invoke();
        return string.Empty;
    }

    /// <summary>Stops without writing.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _wizard.Cancel();
        }

        StepChanged?.Invoke();
    }

    /// <summary>The operator pressed Next; on the last step the result is written. Returns the parameters written and refused.</summary>
    public async Task<IReadOnlyList<ParameterApplyResult>> NextAsync()
    {
        IReadOnlyList<ChannelCalibration>? result = null;
        Dictionary<StickFunction, int?> mapping = [];
        int count;
        lock (_lock)
        {
            if (_wizard.Next())
            {
                result = _wizard.Result();
                foreach (var (function, _) in StickParameters)
                {
                    mapping[function] = _wizard.ChannelOf(function);
                }
            }

            count = _wizard.ChannelCount;
        }

        StepChanged?.Invoke();
        if (result is null || _vehicles.Active is not { } vehicle)
        {
            return [];
        }

        var writes = new List<(string Name, double Value)>();
        for (var i = 0; i < result.Count; i++)
        {
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            writes.Add(($"RC{n}_TRIM", result[i].Trim));
            writes.Add(($"RC{n}_MIN", result[i].Min));
            writes.Add(($"RC{n}_MAX", result[i].Max));
            writes.Add(($"RC{n}_REV", result[i].Reversed ? -1 : 1));
        }

        writes.AddRange(StickParameters.Select(p => (p.Parameter, (double)(mapping[p.Function] is { } channel ? channel + 1 : 0))));
        writes.Add(("RC_CHAN_CNT", count));

        var results = new List<ParameterApplyResult>();
        foreach (var (name, value) in writes)
        {
            if (vehicle.Parameters.Values.GetValueOrDefault(name) is not { } current || ParameterRules.Same(value, current.Value.Value, current.Value.Type))
            {
                continue;
            }

            var written = await vehicle.Parameters.WriteAsync(name, new ParameterValue(current.Value.Type, value));
            results.Add(new ParameterApplyResult(name, written.IsApplied, written.Reason));
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _vehicles.ActiveChanged -= Follow;
        Follow(null);
    }

    private void Follow(Vehicle? vehicle)
    {
        lock (_lock)
        {
            if (_vehicle is not null)
            {
                _vehicle.Session.Status.RcChannels -= OnRc;
            }

            _vehicle = vehicle;
            _channels = [];
            _wizard.Cancel();
            if (vehicle is not null)
            {
                vehicle.Session.Status.RcChannels += OnRc;
            }
        }

        ChannelsChanged?.Invoke();
        StepChanged?.Invoke();
    }

    private void OnRc(IReadOnlyList<ushort> pwm)
    {
        bool stepChanged;
        lock (_lock)
        {
            _channels = pwm;
            stepChanged = _wizard.OnSample([.. pwm.Select(v => (int)v)], _time.GetUtcNow());
        }

        ChannelsChanged?.Invoke();
        if (stepChanged)
        {
            StepChanged?.Invoke();
        }
    }
}
