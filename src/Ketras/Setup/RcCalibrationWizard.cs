using System.Globalization;

namespace Ketras.Setup;

/// <summary>A stick the wizard maps to a channel.</summary>
public enum StickFunction
{
    /// <summary>Roll.</summary>
    Roll,
    /// <summary>Pitch.</summary>
    Pitch,
    /// <summary>Yaw.</summary>
    Yaw,
    /// <summary>Throttle.</summary>
    Throttle
}

/// <summary>A step of the RC calibration.</summary>
public enum WizardStep
{
    /// <summary>SticksNeutral.</summary>
    SticksNeutral,
    /// <summary>ThrottleUp.</summary>
    ThrottleUp,
    /// <summary>ThrottleDown.</summary>
    ThrottleDown,
    /// <summary>YawRight.</summary>
    YawRight,
    /// <summary>YawLeft.</summary>
    YawLeft,
    /// <summary>RollRight.</summary>
    RollRight,
    /// <summary>RollLeft.</summary>
    RollLeft,
    /// <summary>PitchUp.</summary>
    PitchUp,
    /// <summary>PitchDown.</summary>
    PitchDown,
    /// <summary>SwitchesMinMax.</summary>
    SwitchesMinMax,
    /// <summary>Complete.</summary>
    Complete
}

/// <summary>One channel's calibration result.</summary>
public sealed class ChannelCalibration
{
    /// <summary>Lowest PWM.</summary>
    public int Min { get; set; }
    /// <summary>Highest PWM.</summary>
    public int Max { get; set; }
    /// <summary>Centre PWM.</summary>
    public int Trim { get; set; }
    /// <summary>The channel runs backwards.</summary>
    public bool Reversed { get; set; }
    /// <summary>The stick on it, if any.</summary>
    public StickFunction? Function { get; set; }
}

/// <summary>State machine: RC transmitter calibration computed on the ground station, ported from
/// Pure logic; time is passed in.</summary>
public sealed class RcCalibrationWizard
{
    /// <summary>RC_CHANNELS carries 18.</summary>
    public const int MaxChannels = 18;
    /// <summary>The four sticks.</summary>
    public const int MinChannels = 4;

    private const int CenterPoint = 1500;
    private const int DefaultMin = 1000;
    private const int DefaultMax = 2000;
    private const int MoveDelta = 300;
    private const int SettleDelta = 20;
    private const int ValidMin = DefaultMin + (DefaultMax - DefaultMin) * 3 / 10;
    private const int ValidMax = DefaultMax - (DefaultMax - DefaultMin) * 3 / 10;
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1000);

    private static readonly (WizardStep Step, StickFunction? Function)[] Steps =
    [
        (WizardStep.SticksNeutral, null),
        (WizardStep.ThrottleUp, StickFunction.Throttle),
        (WizardStep.ThrottleDown, StickFunction.Throttle),
        (WizardStep.YawRight, StickFunction.Yaw),
        (WizardStep.YawLeft, StickFunction.Yaw),
        (WizardStep.RollRight, StickFunction.Roll),
        (WizardStep.RollLeft, StickFunction.Roll),
        (WizardStep.PitchUp, StickFunction.Pitch),
        (WizardStep.PitchDown, StickFunction.Pitch),
        (WizardStep.SwitchesMinMax, null),
        (WizardStep.Complete, null)
    ];

    private readonly ChannelCalibration[] _channels = Enumerable.Range(0, MaxChannels).Select(_ => new ChannelCalibration()).ToArray();
    private readonly Dictionary<StickFunction, int> _mapping = new();
    private readonly int[] _raw = new int[MaxChannels];
    private readonly int[] _saved = new int[MaxChannels];
    private int _stepIndex = -1;
    private int _detectChannel = -1;
    private int _detectValue;
    private DateTimeOffset? _settleStarted;

    /// <summary>Channels the receiver reports.</summary>
    public int ChannelCount { get; private set; }

    /// <summary>A calibration is under way.</summary>
    public bool IsRunning => _stepIndex >= 0;

    /// <summary>The current step.</summary>
    public WizardStep? Step => IsRunning ? Steps[_stepIndex].Step : null;

    /// <summary>Only the neutral, switches and final steps wait for the operator; stick steps advance by themselves.</summary>
    public bool NextEnabled => Step is WizardStep.SticksNeutral or WizardStep.SwitchesMinMax or WizardStep.Complete;

    /// <summary>The 0-based channel mapped to <paramref name="function"/>.</summary>
    public int? ChannelOf(StickFunction function) => _mapping.TryGetValue(function, out var channel) ? channel : null;

    /// <summary>Starts; needs at least <see cref="MinChannels"/> channels.</summary>
    public void Start()
    {
        if (ChannelCount < MinChannels)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{ChannelCount} RC channels detected; at least {MinChannels} are needed."));
        }

        foreach (var channel in _channels)
        {
            channel.Function = null;
            channel.Reversed = false;
            channel.Min = CenterPoint;
            channel.Max = CenterPoint;
            channel.Trim = CenterPoint;
        }

        _mapping.Clear();
        _stepIndex = 0;
        SetupStep();
    }

    /// <summary>Stops without a result.</summary>
    public void Cancel() => _stepIndex = -1;

    /// <summary>Feeds one RC sample; returns true when the step changed.</summary>
    public bool OnSample(IReadOnlyList<int> pwm, DateTimeOffset now)
    {
        var count = Math.Min(pwm.Count, MaxChannels);
        for (var i = 0; i < count; i++)
        {
            _raw[i] = pwm[i];
        }

        if (!IsRunning)
        {
            ChannelCount = count;
            return false;
        }

        var before = _stepIndex;
        var (step, function) = Steps[_stepIndex];
        for (var channel = 0; channel < count && _stepIndex == before; channel++)
        {
            var value = pwm[channel];
            switch (step)
            {
                case WizardStep.ThrottleUp or WizardStep.YawRight or WizardStep.RollRight or WizardStep.PitchUp:
                    StickDetect(function!.Value, channel, value, now);
                    break;
                case WizardStep.ThrottleDown or WizardStep.YawLeft or WizardStep.RollLeft or WizardStep.PitchDown:
                    StickMin(function!.Value, channel, value, now);
                    break;
                case WizardStep.SwitchesMinMax:
                    SwitchMinMax(channel, value);
                    break;
            }
        }

        return _stepIndex != before;
    }

    /// <summary>Operator pressed Next; returns true when the calibration is complete and must be saved.</summary>
    public bool Next()
    {
        switch (Step)
        {
            case WizardStep.SticksNeutral:
                for (var i = 0; i < ChannelCount; i++)
                {
                    _channels[i].Trim = _raw[i];
                }

                Advance();
                return false;
            case WizardStep.SwitchesMinMax:
                Advance();
                return false;
            case WizardStep.Complete:
                _stepIndex = -1;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Validated per-channel result.</summary>
    public IReadOnlyList<ChannelCalibration> Result()
    {
        var result = new List<ChannelCalibration>();
        for (var i = 0; i < MaxChannels; i++)
        {
            var source = _channels[i];
            var channel = new ChannelCalibration { Min = source.Min, Max = source.Max, Trim = source.Trim, Reversed = source.Reversed, Function = source.Function };
            if (i >= ChannelCount || channel.Min > ValidMin || channel.Max < ValidMax)
            {
                channel.Min = DefaultMin;
                channel.Max = DefaultMax;
                channel.Trim = DefaultMin + (DefaultMax - DefaultMin) / 2;
                if (i >= ChannelCount)
                {
                    channel.Reversed = false;
                    channel.Function = null;
                }
            }
            else if (channel.Function is not null)
            {
                channel.Trim = Math.Clamp(channel.Trim, channel.Min, channel.Max);
            }
            else
            {
                channel.Trim = channel.Min + (channel.Max - channel.Min) / 2;
            }

            result.Add(channel);
        }

        return result;
    }

    private void Advance()
    {
        _stepIndex++;
        if (_stepIndex >= Steps.Length)
        {
            _stepIndex = -1;
            return;
        }

        SetupStep();
    }

    private void SetupStep()
    {
        _detectChannel = -1;
        _settleStarted = null;
        Array.Copy(_raw, _saved, MaxChannels);
    }

    private void StickDetect(StickFunction function, int channel, int value, DateTimeOffset now)
    {
        if (_channels[channel].Function is not null)
        {
            return;
        }

        if (_detectChannel < 0)
        {
            if (Math.Abs(_saved[channel] - value) > MoveDelta)
            {
                _detectChannel = channel;
                _detectValue = value;
            }

            return;
        }

        if (channel != _detectChannel || !SettleComplete(value, now))
        {
            return;
        }

        var info = _channels[channel];
        _mapping[function] = channel;
        info.Function = function;
        info.Reversed = value < _saved[channel];
        if (info.Reversed)
        {
            info.Min = value;
        }
        else
        {
            info.Max = value;
        }

        Advance();
    }

    private void StickMin(StickFunction function, int channel, int value, DateTimeOffset now)
    {
        if (!_mapping.TryGetValue(function, out var mapped) || mapped != channel)
        {
            return;
        }

        var info = _channels[channel];
        if (_detectChannel < 0)
        {
            var moved = info.Reversed ? value > CenterPoint + MoveDelta : value < CenterPoint - MoveDelta;
            if (moved)
            {
                _detectChannel = channel;
                _detectValue = value;
            }

            return;
        }

        if (!SettleComplete(value, now))
        {
            return;
        }

        if (info.Reversed)
        {
            info.Max = value;
        }
        else
        {
            info.Min = value;
        }

        if (function == StickFunction.Throttle)
        {
            info.Trim = value;
        }

        Advance();
    }

    private void SwitchMinMax(int channel, int value)
    {
        var info = _channels[channel];
        if (info.Function is not null || Math.Abs(CenterPoint - value) <= MoveDelta)
        {
            return;
        }

        if (value < CenterPoint)
        {
            info.Min = Math.Min(info.Min, value);
        }
        else
        {
            info.Max = Math.Max(info.Max, value);
        }
    }

    private bool SettleComplete(int value, DateTimeOffset now)
    {
        if (Math.Abs(_detectValue - value) > SettleDelta)
        {
            _detectValue = value;
            _settleStarted = null;
            return false;
        }

        if (_settleStarted is { } started)
        {
            return now - started > SettleTime;
        }

        _settleStarted = now;
        return false;
    }
}
