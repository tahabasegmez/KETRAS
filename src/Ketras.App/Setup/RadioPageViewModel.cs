using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Common;
using Ketras.App.Resources;
using Ketras.Station.Notifications;
using Ketras.Station.Setup;

namespace Ketras.App.Setup;

/// <summary>One channel of the monitor.</summary>
/// <param name="Label">"CH3".</param>
/// <param name="Pwm">Its PWM, µs.</param>
/// <param name="Function">What is mapped to it ("Throttle"), or empty.</param>
public sealed record ChannelBarViewModel(string Label, int Pwm, string Function)
{
    /// <summary>The bar's width, 0 to 1 over 800..2200 µs.</summary>
    public double Fraction => Math.Clamp((Pwm - 800) / 1400.0, 0, 1);

    /// <summary>"1500".</summary>
    public string Value => Pwm.ToString(CultureInfo.CurrentCulture);
}

/// <summary>A mode slot or a switch shown live.</summary>
/// <param name="Label">"3" or "Return".</param>
/// <param name="Detail">The slot's mode or the switch's channel and state.</param>
/// <param name="IsActive">Selected / on.</param>
public sealed record LiveChipViewModel(string Label, string Detail, bool IsActive);

/// <summary>
/// The Radio page (RC and flight modes in one place): the transmitter calibration wizard, the live channel monitor, the
/// stick and auxiliary channel assignment, the six mode slots with the slot the mode channel selects, and the switch
/// assignments with their live state. The wizard writes its result at once; the fields are staged like every edit.
/// </summary>
public sealed partial class RadioPageViewModel : ObservableObject, IDisposable
{
    private static readonly (string Map, string? Threshold, string Label)[] SwitchList =
    [
        ("RC_MAP_ARM_SW", "RC_ARMSWITCH_TH", Strings.RadioSwitchArm),
        ("RC_MAP_KILL_SW", "RC_KILLSWITCH_TH", Strings.RadioSwitchKill),
        ("RC_MAP_RETURN_SW", "RC_RETURN_TH", Strings.RadioSwitchReturn),
        ("RC_MAP_LOITER_SW", "RC_LOITER_TH", Strings.RadioSwitchLoiter),
        ("RC_MAP_OFFB_SW", "RC_OFFB_TH", Strings.RadioSwitchOffboard),
        ("RC_MAP_GEAR_SW", "RC_GEAR_TH", Strings.RadioSwitchGear),
        ("RC_MAP_FLAPS", null, Strings.RadioSwitchFlaps)
    ];

    private readonly SetupContext _context;
    private readonly RadioCalibrationWorkflow _radio;
    private readonly VehicleSetupService _setup;
    private readonly IUiDispatcher _ui;
    private readonly Action<string, NotificationSeverity> _log;

    /// <summary>Follows <paramref name="context"/> and <paramref name="radio"/>.</summary>
    public RadioPageViewModel(SetupContext context, RadioCalibrationWorkflow radio, VehicleSetupService setup, IUiDispatcher ui, Action<string, NotificationSeverity> log)
    {
        _context = context;
        _radio = radio;
        _setup = setup;
        _ui = ui;
        _log = log;
        ParameterFieldViewModel F(string name, string label) => new(name, context, label);
        Mapping = new ParameterSectionViewModel(Strings.RadioMapping,
        [
            F("COM_RC_IN_MODE", Strings.RadioInputMode), F("RC_MAP_ROLL", Strings.RadioRoll), F("RC_MAP_PITCH", Strings.RadioPitch), F("RC_MAP_YAW", Strings.RadioYaw),
            F("RC_MAP_THROTTLE", Strings.RadioThrottle), F("RC_MAP_AUX1", Strings.RadioAux1), F("RC_MAP_AUX2", Strings.RadioAux2),
            F("RC_MAP_PARAM1", Strings.RadioKnob1), F("RC_MAP_PARAM2", Strings.RadioKnob2), F("RC_MAP_PARAM3", Strings.RadioKnob3)
        ]);
        Modes = new ParameterSectionViewModel(Strings.RadioModes,
        [
            F("RC_MAP_FLTMODE", Strings.RadioModeChannel),
            .. Enumerable.Range(1, RcRules.SlotCount).Select(i => F(string.Create(CultureInfo.InvariantCulture, $"COM_FLTMODE{i}"),
                string.Format(CultureInfo.CurrentCulture, Strings.RadioSlot, i)))
        ]);
        Switches = new ParameterSectionViewModel(Strings.RadioSwitches, [.. SwitchList.Select(s => F(s.Map, s.Label))]);
        context.ValuesChanged += _ => Refresh();
        context.StateChanged += Refresh;
        radio.ChannelsChanged += OnChannels;
        radio.StepChanged += OnStep;
        Refresh();
    }

    /// <summary>The page title.</summary>
    public string Title => Strings.SetupPageRadio;

    /// <summary>Stick and auxiliary channels.</summary>
    public ParameterSectionViewModel Mapping { get; }

    /// <summary>The mode channel and its six slots.</summary>
    public ParameterSectionViewModel Modes { get; }

    /// <summary>The switches.</summary>
    public ParameterSectionViewModel Switches { get; }

    /// <summary>The live channels.</summary>
    public ObservableCollection<ChannelBarViewModel> Channels { get; } = [];

    /// <summary>No RC input arrives.</summary>
    [ObservableProperty] public partial bool NoInput { get; private set; } = true;

    /// <summary>The six slots, the selected one active.</summary>
    public ObservableCollection<LiveChipViewModel> Slots { get; } = [];

    /// <summary>The assigned switches and their state.</summary>
    public ObservableCollection<LiveChipViewModel> SwitchStates { get; } = [];

    /// <summary>RC is not used (manual control over MAVLink or off), so calibration is optional.</summary>
    [ObservableProperty] public partial string InputNote { get; private set; } = string.Empty;

    /// <summary>The wizard runs.</summary>
    [ObservableProperty] public partial bool IsCalibrating { get; private set; }

    /// <summary>What to do in this step.</summary>
    [ObservableProperty] public partial string Instruction { get; private set; } = string.Empty;

    /// <summary>Next is possible.</summary>
    [ObservableProperty] public partial bool CanNext { get; private set; }

    /// <summary>Calibrate and Copy trims are possible.</summary>
    [ObservableProperty] public partial bool CanStart { get; private set; }

    /// <summary>Why the wizard did not start; empty otherwise.</summary>
    [ObservableProperty] public partial string Error { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
        _radio.ChannelsChanged -= OnChannels;
        _radio.StepChanged -= OnStep;
    }

    /// <summary>Re-evaluates the page.</summary>
    public void Refresh()
    {
        Mapping.Refresh();
        Modes.Refresh();
        Switches.Refresh();
        var mode = _context.Value("COM_RC_IN_MODE");
        InputNote = mode is { } m && !SetupRules.RadioUsed(_context.Reader) && _context.TypeOf("COM_RC_IN_MODE") is { } type
            ? string.Format(CultureInfo.CurrentCulture, Strings.RadioNotUsed, ParameterText.Format(m, type, _context.MetadataOf("COM_RC_IN_MODE")))
            : string.Empty;
        UpdateWizard();
        UpdateLive();
    }

    [RelayCommand]
    private void Calibrate()
    {
        Error = _radio.Start();
        UpdateWizard();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        var results = await _radio.NextAsync();
        if (results.Count > 0 || _radio.Step is null && IsCalibrating)
        {
            var refused = results.Where(r => !r.IsApplied).ToList();
            _log(refused.Count == 0 ? string.Format(CultureInfo.CurrentCulture, Strings.RadioWritten, results.Count)
                : string.Format(CultureInfo.CurrentCulture, Strings.RadioNotWritten, string.Join(", ", refused.Select(r => r.Name))),
                refused.Count == 0 ? NotificationSeverity.Success : NotificationSeverity.Error);
        }

        UpdateWizard();
    }

    [RelayCommand]
    private void CancelCalibration()
    {
        _radio.Cancel();
        _log(Strings.RadioCancelled, NotificationSeverity.Warning);
        UpdateWizard();
    }

    [RelayCommand]
    private async Task CopyTrimsAsync()
    {
        var result = await _setup.CopyTrimsAsync();
        _log(result.IsAccepted ? string.Format(CultureInfo.CurrentCulture, Strings.SetupActionDone, Strings.RadioCopyTrims)
            : string.Format(CultureInfo.CurrentCulture, Strings.SetupActionRefused, Strings.RadioCopyTrims, result.Message),
            result.IsAccepted ? NotificationSeverity.Success : NotificationSeverity.Error);
    }

    private void OnChannels() => _ui.Post(UpdateLive);

    private void OnStep() => _ui.Post(UpdateWizard);

    private void UpdateWizard()
    {
        var step = _radio.Step;
        IsCalibrating = step is not null;
        CanNext = IsCalibrating && _radio.CanNext;
        CanStart = !IsCalibrating && _context.CanEdit;
        Instruction = step switch
        {
            WizardStep.SticksNeutral => Strings.RadioStepNeutral,
            WizardStep.ThrottleUp => Strings.RadioStepThrottleUp,
            WizardStep.ThrottleDown => Strings.RadioStepThrottleDown,
            WizardStep.YawRight => Strings.RadioStepYawRight,
            WizardStep.YawLeft => Strings.RadioStepYawLeft,
            WizardStep.RollRight => Strings.RadioStepRollRight,
            WizardStep.RollLeft => Strings.RadioStepRollLeft,
            WizardStep.PitchUp => Strings.RadioStepPitchUp,
            WizardStep.PitchDown => Strings.RadioStepPitchDown,
            WizardStep.SwitchesMinMax => Strings.RadioStepSwitches,
            WizardStep.Complete => Strings.RadioStepComplete,
            _ => string.Empty
        };
    }

    private void UpdateLive()
    {
        var pwm = _radio.Channels;
        NoInput = pwm.Count == 0;
        var functions = RadioCalibrationWorkflow.StickParameters
            .Select(p => (Channel: (int?)_context.Value(p.Parameter), Label: Label(p.Function)))
            .Where(p => p.Channel > 0).ToDictionary(p => p.Channel!.Value, p => p.Label);
        Sync(Channels, [.. pwm.Select((value, i) => new ChannelBarViewModel(string.Create(CultureInfo.InvariantCulture, $"CH{i + 1}"), value,
            functions.GetValueOrDefault(i + 1, string.Empty)))]);

        int? Pwm(double? channel) => channel is { } c && c > 0 && (int)c <= pwm.Count ? pwm[(int)c - 1] : null;
        var modeChannel = _context.Value("RC_MAP_FLTMODE");
        var slot = 0;
        if (Pwm(modeChannel) is { } modePwm)
        {
            var n = ((int)modeChannel!.Value).ToString(CultureInfo.InvariantCulture);
            slot = RcRules.ActiveSlot(modePwm, _context.Value($"RC{n}_MIN") ?? 1000, _context.Value($"RC{n}_MAX") ?? 2000,
                _context.Value($"RC{n}_TRIM") ?? 1500, _context.Value($"RC{n}_REV") ?? 1);
        }

        Sync(Slots, [.. Enumerable.Range(1, RcRules.SlotCount).Select(i =>
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"COM_FLTMODE{i}");
            var mode = _context.Value(name) is { } v && _context.TypeOf(name) is { } t ? ParameterText.Format(v, t, _context.MetadataOf(name)) : "--";
            return new LiveChipViewModel(i.ToString(CultureInfo.CurrentCulture), mode, i == slot);
        })]);

        var switches = new List<LiveChipViewModel>();
        foreach (var (map, threshold, label) in SwitchList)
        {
            if (_context.Value(map) is not (> 0 and var channel))
            {
                continue;
            }

            var value = Pwm(channel);
            var on = value is { } p && threshold is not null && RcRules.SwitchOn(p, _context.Value(threshold) ?? 0.5);
            var state = value is not { } v2 ? "--" : threshold is null ? $"{v2} µs" : on ? Strings.RadioOn : Strings.RadioOff;
            switches.Add(new LiveChipViewModel(label, string.Create(CultureInfo.CurrentCulture, $"ch{channel} {state}"), on));
        }

        Sync(SwitchStates, switches);
    }

    private static string Label(StickFunction function) => function switch
    {
        StickFunction.Roll => Strings.RadioRoll,
        StickFunction.Pitch => Strings.RadioPitch,
        StickFunction.Yaw => Strings.RadioYaw,
        _ => Strings.RadioThrottle
    };

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count)
            {
                if (!Equals(target[i], items[i]))
                {
                    target[i] = items[i];
                }
            }
            else
            {
                target.Add(items[i]);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
