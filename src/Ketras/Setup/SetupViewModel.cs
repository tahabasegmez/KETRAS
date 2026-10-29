using System.Globalization;
using Ketras.Controls;
using Ketras.Mavlink.Messages;
using Ketras.Notifications;
using Ketras.Parameters;
using Ketras.Resources;
using Ketras.Settings;
using Ketras.Utilities;
using Ketras.Vehicles;

namespace Ketras.Setup;

/// <summary>
/// The Vehicle setup activity: Summary (identity and checklist), the task pages and Parameters, with the banner (edit
/// state, pending changes) over them and the setup log beside them: writes, refusals, actions and the vehicle's own
/// status texts.
/// </summary>
public sealed class SetupViewModel : SplitScreenViewModel, ILeaveAware, IDisposable
{
    private readonly SetupContext _context;
    private readonly IUiDispatcher _ui;
    private Vehicle? _vehicle;

    /// <summary>Builds the pages over <paramref name="context"/>.</summary>
    public SetupViewModel(SetupContext context, VehicleSetupService setup, CalibrationWorkflow calibration, RadioCalibrationWorkflow radio, ActuatorTestWorkflow testing, SettingsStore settings, IFileDialogService files,
        ReadinessViewModel readiness, IUiDispatcher ui, TimeProvider time)
        : base(time)
    {
        _context = context;
        _ui = ui;
        BannerModel = new SetupBannerViewModel(context, LogApply);
        Summary = new SummaryPageViewModel(context, readiness);
        Sensors = new SensorsPageViewModel(context, calibration, setup, ui, Log.Append);
        Radio = new RadioPageViewModel(context, radio, setup, ui, Log.Append);
        Power = new PowerPageViewModel(context, ui, settings, time);
        Actuators = new ActuatorsPageViewModel(context, testing, ui, Log.Append);
        Parameters = new ParametersPageViewModel(context, setup, settings, files, Log.Append);
        var summaryPage = new ScreenPage(Strings.SetupPageSummary, "ClipboardCheck", Summary);
        var sensorsPage = new ScreenPage(Strings.SetupPageSensors, "Compass", Sensors);
        var radioPage = new ScreenPage(Strings.SetupPageRadio, "Gamepad2", Radio);
        var powerPage = new ScreenPage(Strings.SetupPagePower, "BatteryMedium", Power);
        var actuatorsPage = new ScreenPage(Strings.SetupPageActuators, "Cog", Actuators);
        Pages = [summaryPage, sensorsPage, radioPage, powerPage, actuatorsPage, new ScreenPage(Strings.SetupPageParameters, "ListTree", Parameters)];
        Summary.Checklist.Add(new ChecklistRowViewModel(sensorsPage, SetupRules.Sensors, page => SelectedPage = page));
        Summary.Checklist.Add(new ChecklistRowViewModel(radioPage, SetupRules.Radio, page => SelectedPage = page));
        Summary.Checklist.Add(new ChecklistRowViewModel(powerPage, SetupRules.Power, page => SelectedPage = page));
        Summary.Checklist.Add(new ChecklistRowViewModel(actuatorsPage, _ => Actuators.Status, page => SelectedPage = page));
        SelectedPage = summaryPage;
        Summary.Refresh();

        context.StateChanged += OnState;
        OnState();
    }

    /// <inheritdoc />
    public override string NavTitle => Strings.SetupNavTitle;

    /// <inheritdoc />
    public override string LogTitle => Strings.SetupLogTitle;

    /// <inheritdoc />
    public override string LogEmptyText => Strings.SetupLogEmpty;

    /// <inheritdoc />
    public override IReadOnlyList<ScreenPage> Pages { get; }

    /// <inheritdoc />
    public override object? Banner => BannerModel;

    /// <inheritdoc />
    public override System.Windows.Input.ICommand? NavCommand => BannerModel.RefreshCommand;

    /// <inheritdoc />
    public override string NavIcon => "RefreshCw";

    /// <inheritdoc />
    public override string NavTip => Strings.SetupRefreshTip;

    /// <summary>The banner.</summary>
    public SetupBannerViewModel BannerModel { get; }

    /// <summary>The Summary page.</summary>
    public SummaryPageViewModel Summary { get; }

    /// <summary>The Sensors page.</summary>
    public SensorsPageViewModel Sensors { get; }

    /// <summary>The Radio page.</summary>
    public RadioPageViewModel Radio { get; }

    /// <summary>The Power page.</summary>
    public PowerPageViewModel Power { get; }

    /// <summary>The Actuators page.</summary>
    public ActuatorsPageViewModel Actuators { get; }

    /// <summary>The Parameters page.</summary>
    public ParametersPageViewModel Parameters { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _context.StateChanged -= OnState;
        Sensors.Dispose();
        Radio.Dispose();
        Power.Dispose();
        Actuators.Dispose();
        Follow(null);
    }

    /// <inheritdoc />
    public void OnLeft() => Actuators.Leave();

    /// <summary>Leaving the Actuators page hands every tested output back to the vehicle.</summary>
    protected override void OnPageChanged(ScreenPage? from)
    {
        if (from?.Content is ActuatorsPageViewModel actuators)
        {
            actuators.Leave();
        }
    }

    private void OnState()
    {
        if (!ReferenceEquals(_context.Vehicle, _vehicle))
        {
            Follow(_context.Vehicle);
        }
    }

    private void Follow(Vehicle? vehicle)
    {
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

    private void OnStatusText(MavSeverity severity, string text) => _ui.Post(() => Log.Append(text, severity switch
    {
        <= MavSeverity.Error => NotificationSeverity.Error,
        MavSeverity.Warning => NotificationSeverity.Warning,
        _ => NotificationSeverity.Info
    }));

    private void LogApply(IReadOnlyList<ParameterApplyResult> results)
    {
        foreach (var result in results)
        {
            if (result.IsApplied)
            {
                var value = _context.Value(result.Name) is { } v && _context.TypeOf(result.Name) is { } type
                    ? ParameterText.Format(v, type, _context.MetadataOf(result.Name)) : string.Empty;
                Log.Append(string.Format(CultureInfo.CurrentCulture, Strings.SetupLogWritten, result.Name, value), NotificationSeverity.Success);
            }
            else
            {
                Log.Append(string.Format(CultureInfo.CurrentCulture, Strings.SetupLogRefused, result.Name, result.Reason), NotificationSeverity.Error);
            }
        }
    }
}
