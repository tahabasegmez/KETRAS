using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ketras.Resources;
using Ketras.Telemetry;
using Ketras.Utilities;
using Ketras.Vehicles;

namespace Ketras.Shell;

/// <summary>
/// The flight items of the status bar, while a vehicle is active: satellites and HDOP, the link's packet loss, and the
/// time since arming. GPS and link take the level of their <see cref="FlightHealthRules"/> check (Warning / Error), with
/// the check's detail as the tooltip. Read from <see cref="FlightMonitorWorker"/> once a second.
/// </summary>
public sealed partial class FlightBarViewModel : ObservableObject, IDisposable
{
    private readonly VehicleRegistry _vehicles;
    private readonly FlightMonitorWorker _flight;
    private readonly IUiDispatcher _ui;

    /// <summary>Follows the flight monitor.</summary>
    public FlightBarViewModel(VehicleRegistry vehicles, FlightMonitorWorker flight, IUiDispatcher ui)
    {
        _vehicles = vehicles;
        _flight = flight;
        _ui = ui;
        _flight.Changed += OnFlight;
        Apply(flight.Status);
    }

    /// <summary>A vehicle is active.</summary>
    [ObservableProperty] public partial bool HasVehicle { get; private set; }

    /// <summary>"14 sat · HDOP 0.8", or "No GPS".</summary>
    [ObservableProperty] public partial string Gps { get; private set; } = string.Empty;

    /// <summary>The GPS check's level.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGpsWarning), nameof(IsGpsError))]
    public partial CheckLevel GpsLevel { get; private set; }

    /// <summary>The GPS item warns.</summary>
    public bool IsGpsWarning => GpsLevel == CheckLevel.Warning;

    /// <summary>The GPS item is an error.</summary>
    public bool IsGpsError => GpsLevel == CheckLevel.Error;

    /// <summary>The GPS check's detail.</summary>
    [ObservableProperty] public partial string GpsTip { get; private set; } = string.Empty;

    /// <summary>"2 % loss"; empty before it is measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink))]
    public partial string Link { get; private set; } = string.Empty;

    /// <summary>The link loss is measured.</summary>
    public bool HasLink => Link.Length > 0;

    /// <summary>The link check's level.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinkWarning), nameof(IsLinkError))]
    public partial CheckLevel LinkLevel { get; private set; }

    /// <summary>The link item warns.</summary>
    public bool IsLinkWarning => LinkLevel == CheckLevel.Warning;

    /// <summary>The link item is an error.</summary>
    public bool IsLinkError => LinkLevel == CheckLevel.Error;

    /// <summary>The link check's detail.</summary>
    [ObservableProperty] public partial string LinkTip { get; private set; } = string.Empty;

    /// <summary>Time since arming; empty while disarmed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArmed))]
    public partial string FlightTime { get; private set; } = string.Empty;

    /// <summary>The vehicle is armed.</summary>
    public bool IsArmed => FlightTime.Length > 0;

    /// <inheritdoc />
    public void Dispose() => _flight.Changed -= OnFlight;

    /// <summary>Takes a reading; call on the UI thread.</summary>
    internal void Apply(FlightStatus status)
    {
        var snapshot = _vehicles.Active?.Snapshot;
        HasVehicle = snapshot is not null;
        Gps = snapshot is null ? string.Empty
            : snapshot.Fix == PositionFix.None && snapshot.Satellites is null ? Strings.StatusGpsNone
            : string.Format(CultureInfo.CurrentCulture, Strings.StatusGps, snapshot.Satellites?.ToString(CultureInfo.CurrentCulture) ?? "?",
                snapshot.Hdop?.ToString("F1", CultureInfo.CurrentCulture) ?? "?");
        var gps = status.Checks.FirstOrDefault(c => c.Code == "vehicle.gps");
        GpsLevel = gps?.Level ?? CheckLevel.Unknown;
        GpsTip = gps is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Strings.StatusGpsTip, gps.Detail);
        Link = status.Link?.LossPercent is { } loss ? string.Format(CultureInfo.CurrentCulture, Strings.StatusLink, loss) : string.Empty;
        var link = status.Checks.FirstOrDefault(c => c.Code == "vehicle.link");
        LinkLevel = link?.Level ?? CheckLevel.Unknown;
        LinkTip = link is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Strings.StatusLinkTip, link.Detail);
        FlightTime = status.FlightTime is { } time ? TelemetryPanelViewModel.Duration(time.TotalSeconds) : string.Empty;
    }

    private void OnFlight(FlightStatus status) => _ui.Post(() => Apply(status));
}
