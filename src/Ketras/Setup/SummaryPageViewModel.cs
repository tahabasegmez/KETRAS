using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Controls;
using Ketras.Parameters;
using Ketras.Resources;

namespace Ketras.Setup;

/// <summary>A key / value row of the Summary.</summary>
/// <param name="Key">The key.</param>
/// <param name="Value">The value.</param>
public sealed record SummaryRow(string Key, string Value);

/// <summary>A failing check of the vehicle's health and arming report.</summary>
/// <param name="Message">What fails.</param>
/// <param name="Detail">Where, and which modes it stops; empty when unknown.</param>
/// <param name="IsError">It stops the modes it names (else a warning).</param>
public sealed record HealthRow(string Message, string Detail, bool IsError);

/// <summary>A setup task of the checklist.</summary>
public sealed partial class ChecklistRowViewModel(ScreenPage page, Func<ParameterReader, SetupState> rule, Action<ScreenPage> open) : ObservableObject
{
    /// <summary>The task's page title.</summary>
    public string Title => page.Title;

    /// <summary>Its icon.</summary>
    public string Icon => page.Icon;

    /// <summary>Its state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsRequired))]
    public partial SetupState State { get; private set; }

    /// <summary>"Ready", "Setup required" or "Not applicable".</summary>
    [ObservableProperty] public partial string StateText { get; private set; } = string.Empty;

    /// <summary>Done.</summary>
    public bool IsReady => State == SetupState.Ready;

    /// <summary>Still to do.</summary>
    public bool IsRequired => State == SetupState.Required;

    /// <summary>Evaluates the rule; the page is marked while setup is required.</summary>
    public void Refresh(ParameterReader reader, bool hasValues)
    {
        State = hasValues ? rule(reader) : SetupState.None;
        StateText = State switch
        {
            SetupState.Ready => Strings.SetupStateReady,
            SetupState.Required => Strings.SetupStateRequired,
            _ => Strings.SetupStateNone
        };
        page.NeedsAttention = State == SetupState.Required;
    }

    [RelayCommand]
    private void Open() => open(page);
}

/// <summary>
/// The Summary page: the vehicle's identity (firmware, system id, airframe), the checks that fail in its health and
/// arming report (PX4 events), and the setup checklist.
/// </summary>
public sealed partial class SummaryPageViewModel : ObservableObject
{
    private readonly SetupContext _context;

    /// <summary>Follows <paramref name="context"/>; <paramref name="readiness"/> is the Ready to fly list, when there is one.</summary>
    public SummaryPageViewModel(SetupContext context, ReadinessViewModel? readiness = null)
    {
        _context = context;
        Readiness = readiness;
        context.ValuesChanged += _ => Refresh();
        context.StateChanged += Refresh;
        context.HealthChanged += RefreshHealth;
    }

    /// <summary>The Ready to fly list; null in tests without the station.</summary>
    public ReadinessViewModel? Readiness { get; }

    /// <summary>The failing checks, errors first; empty when all pass or the vehicle reports none.</summary>
    public ObservableCollection<HealthRow> Health { get; } = [];

    /// <summary>The vehicle reports its checks (PX4 events).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHealthy))]
    public partial bool HasHealthReport { get; private set; }

    /// <summary>A report with no failing check.</summary>
    public bool IsHealthy => HasHealthReport && Health.Count == 0;

    /// <summary>The page title.</summary>
    public string Title => Strings.SetupPageSummary;

    /// <summary>Identity rows.</summary>
    public ObservableCollection<SummaryRow> Identity { get; } = [];

    /// <summary>The setup tasks.</summary>
    public ObservableCollection<ChecklistRowViewModel> Checklist { get; } = [];

    /// <summary>Configuration problems at the airframe level; empty when none.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>There are warnings.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Re-reads everything.</summary>
    public void Refresh()
    {
        var vehicle = _context.Vehicle;
        var reader = _context.Reader;
        Identity.Clear();
        string Param(string name) => _context.Value(name) is { } value && _context.TypeOf(name) is { } type
            ? ParameterText.Format(value, type, _context.MetadataOf(name)) : "--";
        Identity.Add(new SummaryRow(Strings.SummaryFirmware, vehicle?.Session.Status.Firmware is { } firmware ? $"PX4 {firmware.Version}" : "--"));
        Identity.Add(new SummaryRow(Strings.SummarySystemId, vehicle?.Id.SystemId.ToString(CultureInfo.CurrentCulture) ?? "--"));
        Identity.Add(new SummaryRow(Strings.SummaryAirframe, Param("SYS_AUTOSTART")));
        Identity.Add(new SummaryRow(Strings.SummaryVehicleType, Param("CA_AIRFRAME")));
        Identity.Add(new SummaryRow(Strings.SummarySurfaces, Param("CA_SV_CS_COUNT")));
        Identity.Add(new SummaryRow(Strings.SummaryMotors, Param("CA_ROTOR_COUNT")));
        Identity.Add(new SummaryRow(Strings.SummaryParameters, _context.Values.Count > 0 ? _context.Values.Count.ToString(CultureInfo.CurrentCulture) : "--"));
        Identity.Add(new SummaryRow(Strings.SummaryDescriptions, _context.Metadata.Source switch
        {
            MetadataSource.Vehicle => Strings.SummaryDescriptionsVehicle,
            MetadataSource.Download => Strings.SummaryDescriptionsDownload,
            _ => _context.IsMetadataLoading ? Strings.SetupLoadingMetadata : Strings.SummaryDescriptionsNone
        }));

        Warnings.Clear();
        if (_context.Values.Count > 0)
        {
            if (SetupRules.Airframe(reader) == SetupState.Required)
            {
                Warnings.Add(Strings.SummaryNoAirframe);
            }

            if (SetupRules.IsNotFixedWing(reader))
            {
                Warnings.Add(Strings.SummaryNotFixedWing);
            }
        }

        OnPropertyChanged(nameof(HasWarnings));
        foreach (var row in Checklist)
        {
            row.Refresh(reader, _context.Values.Count > 0);
        }

        RefreshHealth();
    }

    private void RefreshHealth()
    {
        Health.Clear();
        var report = _context.Vehicle?.Health;
        foreach (var check in report?.Checks.OrderByDescending(check => check.Severity).ToList() ?? [])
        {
            var modes = report!.ModeGroups.Where(group => check.Affects(group.Key)).Select(group => group.Value.Description).ToList();
            var detail = string.Join(" · ", new[] { check.Component, modes.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.SummaryHealthModes, string.Join(", ", modes)) : null }
                .Where(part => !string.IsNullOrEmpty(part)));
            Health.Add(new HealthRow(check.Message, detail, check.Severity == Notifications.NotificationSeverity.Error));
        }

        HasHealthReport = report is not null;
        OnPropertyChanged(nameof(IsHealthy));
    }
}
