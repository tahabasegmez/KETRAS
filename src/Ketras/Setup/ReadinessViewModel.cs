using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ketras.Model.Missions;
using Ketras.Model.Vehicles;
using Ketras.Resources;
using Ketras.Station.Missions;
using Ketras.Station.Vehicles;
using Ketras.Utilities;

namespace Ketras.Setup;

/// <summary>One line of the Ready to fly list.</summary>
public sealed partial class ReadyRowViewModel(string code, string title) : ObservableObject
{
    /// <summary>Which check, e.g. "vehicle.gps".</summary>
    public string Code { get; } = code;

    /// <summary>What is checked.</summary>
    public string Title { get; } = title;

    /// <summary>The outcome.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPass), nameof(IsWarning), nameof(IsError), nameof(IsUnknown))]
    public partial CheckLevel Level { get; set; }

    /// <summary>The values behind it.</summary>
    [ObservableProperty] public partial string Detail { get; set; } = string.Empty;

    /// <summary>It passes.</summary>
    public bool IsPass => Level == CheckLevel.Pass;

    /// <summary>It warns.</summary>
    public bool IsWarning => Level == CheckLevel.Warning;

    /// <summary>It fails.</summary>
    public bool IsError => Level == CheckLevel.Error;

    /// <summary>Not known yet.</summary>
    public bool IsUnknown => Level == CheckLevel.Unknown;
}

/// <summary>
/// The Ready to fly list of the Summary page: the vehicle's arming report, its health checks (GPS, estimator, vibration,
/// battery, link) from <see cref="FlightMonitorWorker"/>, whether the mission is on the vehicle, and the plan's energy
/// margin and headwind from <see cref="PlanValidationService"/>. It adds no rules of its own; rows are updated in place.
/// </summary>
public sealed class ReadinessViewModel : IDisposable
{
    private readonly VehicleRegistry _vehicles;
    private readonly FlightMonitorWorker _flight;
    private readonly PlanValidationService _validation;
    private readonly MissionProgressTracker _progress;
    private readonly IUiDispatcher _ui;

    /// <summary>Follows the flight monitor and the plan checks.</summary>
    public ReadinessViewModel(VehicleRegistry vehicles, FlightMonitorWorker flight, PlanValidationService validation, MissionProgressTracker progress,
        IUiDispatcher ui)
    {
        _vehicles = vehicles;
        _flight = flight;
        _validation = validation;
        _progress = progress;
        _ui = ui;
        Rows =
        [
            new("arming", Strings.SummaryReadyArming),
            new("vehicle.gps", "GPS"),
            new("vehicle.estimator", "Estimator"),
            new("vehicle.vibration", "Vibration"),
            new("vehicle.battery", "Battery"),
            new("vehicle.link", "Link"),
            new("mission", Strings.SummaryReadyMission),
            new("energy", Strings.SummaryReadyEnergy),
            new("wind", Strings.SummaryReadyWind)
        ];
        _flight.Changed += OnFlight;
        _validation.Changed += OnFindings;
        Refresh();
    }

    /// <summary>The checks in a fixed order.</summary>
    public ObservableCollection<ReadyRowViewModel> Rows { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _flight.Changed -= OnFlight;
        _validation.Changed -= OnFindings;
    }

    /// <summary>Re-reads every check; call on the UI thread.</summary>
    public void Refresh()
    {
        var vehicle = _vehicles.Active;
        var status = _flight.Status;
        foreach (var row in Rows.Where(r => r.Code.StartsWith("vehicle.", StringComparison.Ordinal)))
        {
            var check = vehicle is null ? null : status.Checks.FirstOrDefault(c => c.Code == row.Code);
            row.Level = check?.Level ?? CheckLevel.Unknown;
            row.Detail = check?.Detail ?? string.Empty;
        }

        // Arming in the current mode, as the vehicle's health and arming report says (the checks are listed above).
        var arming = Row("arming");
        var report = vehicle?.Health;
        var group = vehicle?.ModeGroupOf(vehicle.Snapshot.Mode);
        var failing = report is not null && group is { } g && !report.CanArm(g)
            ? report.ChecksFor(g).Count(check => check.Severity == Station.Notifications.NotificationSeverity.Error)
            : (int?)null;
        arming.Level = vehicle is null || report is null || group is null ? CheckLevel.Unknown : failing is null ? CheckLevel.Pass : CheckLevel.Error;
        arming.Detail = vehicle is null ? string.Empty
            : report is null || group is null ? Strings.SummaryReadyArmingUnknown
            : failing is null ? Strings.SummaryReadyArmingPass
            : string.Format(CultureInfo.CurrentCulture, Strings.SummaryReadyArmingFail, failing);

        var mission = Row("mission");
        var uploaded = vehicle is null ? null : _progress.Uploaded(vehicle.Id);
        mission.Level = vehicle is null ? CheckLevel.Unknown : uploaded is null ? CheckLevel.Warning : CheckLevel.Pass;
        mission.Detail = vehicle is null ? string.Empty
            : uploaded is null ? Strings.SummaryReadyMissionNone
            : string.Format(CultureInfo.CurrentCulture, Strings.SummaryReadyMissionUploaded, uploaded.Items.Count);

        var energy = Row("energy");
        if (_validation.Budget is { } budget)
        {
            energy.Level = budget.MarginMah < 0 ? CheckLevel.Error : budget.MarginPercent < EnergyCalculator.WarningMarginPercent ? CheckLevel.Warning : CheckLevel.Pass;
            energy.Detail = string.Format(CultureInfo.CurrentCulture, Strings.SummaryReadyEnergyDetail, budget.MarginPercent, budget.NeededMah, budget.UsableMah);
        }
        else
        {
            energy.Level = CheckLevel.Unknown;
            energy.Detail = Strings.SummaryReadyEnergyUnknown;
        }

        var wind = Row("wind");
        var headwind = _validation.Estimate.StrongestHeadwind?.HeadwindRatio ?? 0;
        wind.Level = headwind >= 1 ? CheckLevel.Error : headwind >= MissionBudgetRules.HeadwindRatio ? CheckLevel.Warning : CheckLevel.Pass;
        wind.Detail = headwind > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.SummaryReadyWindDetail, headwind * 100) : Strings.SummaryReadyWindCalm;
    }

    private ReadyRowViewModel Row(string code) => Rows.First(r => r.Code == code);

    private void OnFlight(FlightStatus status) => _ui.Post(Refresh);

    private void OnFindings(IReadOnlyList<Model.Planning.PlanFinding> findings) => _ui.Post(Refresh);
}
