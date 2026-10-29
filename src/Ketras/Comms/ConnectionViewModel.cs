using System.Globalization;
using Ketras.Controls;
using Ketras.Notifications;
using Ketras.Resources;
using Ketras.Simulation;
using Ketras.Utilities;
using Ketras.Vehicles;

namespace Ketras.Comms;

/// <summary>
/// The Vehicle link activity: a page list (Status, Links, SITL), the page, and the link log behind a splitter: link
/// states and steps, plus what <see cref="LinkLogStore"/> holds (vehicle messages, commands and their outcomes). It
/// opens and closes links through <see cref="ConnectionService"/> on the operator's request only, and asks first when
/// that would cut the link to an armed vehicle.
/// </summary>
public sealed partial class ConnectionViewModel : SplitScreenViewModel, IDisposable
{
    private readonly ConnectionService _connections;
    private readonly LinkProfileStore _store;
    private readonly VehicleRegistry _vehicles;
    private readonly LinkDiagnosticsWorkflow _diagnostics;
    private readonly LinkLogStore _linkLog;
    private readonly IUiDispatcher _ui;
    private LinkState _lastState;
    private Dictionary<string, LinkStepState> _lastSteps = [];

    /// <summary>Follows the link and its diagnostics.</summary>
    public ConnectionViewModel(ConnectionService connections, LinkProfileStore store, VehicleRegistry vehicles, LinkDiagnosticsWorkflow diagnostics,
        LinkLogStore linkLog, IUiDispatcher ui, TimeProvider time)
        : base(time)
    {
        _connections = connections;
        _store = store;
        _vehicles = vehicles;
        _diagnostics = diagnostics;
        _linkLog = linkLog;
        _ui = ui;
        Status = new LinkStatusPageViewModel();
        Links = new LinksPageViewModel(store, [.. connections.Simulators.Select(s => s.Description)], ConnectAsync);
        Simulators = new SimulatorsPageViewModel([.. connections.Simulators.Select(s => new SimulatorViewModel(s, ui, Log.Append))]);
        Pages =
        [
            new ScreenPage(Strings.LinkPageStatus, "Activity", Status),
            new ScreenPage(Strings.LinkPageLinks, "Link", Links),
            new ScreenPage(Strings.LinkPageSimulators, "Cpu", Simulators)
        ];
        SelectedPage = Pages[0];

        Status.DisconnectRequested += RequestDisconnect;
        _connections.StatusChanged += OnStatus;
        _diagnostics.Changed += OnDiagnostics;
        _store.Changed += OnProfilesChanged;
        ApplyStatus(connections.Status);
        ApplyDiagnostics();
        foreach (var entry in linkLog.Entries)
        {
            Log.Append(entry.Text, entry.Severity, entry.TimeUtc);
        }

        _linkLog.Appended += OnLinkLog;
    }

    /// <inheritdoc />
    public override string NavTitle => Strings.LinkNavTitle;

    /// <inheritdoc />
    public override string LogTitle => Strings.LinkLogTitle;

    /// <inheritdoc />
    public override string LogEmptyText => Strings.LinkLogEmpty;

    /// <inheritdoc />
    public override IReadOnlyList<ScreenPage> Pages { get; }

    /// <summary>The Status page.</summary>
    public LinkStatusPageViewModel Status { get; }

    /// <summary>The Links page.</summary>
    public LinksPageViewModel Links { get; }

    /// <summary>The SITL page.</summary>
    public SimulatorsPageViewModel Simulators { get; }

    /// <summary>Connects <paramref name="profile"/>, after a confirmation when that closes the link to an armed vehicle; the Status page comes up to follow it.</summary>
    public Task ConnectAsync(LinkProfile profile)
    {
        SelectedPage = Pages[0];
        var current = _connections.Status;
        if (IsArmed && current.State is LinkState.Connected or LinkState.Lost && current.Profile?.Name != profile.Name)
        {
            Status.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.LinkConfirmSwitch, profile.Name), Strings.LinkConfirmSwitchAction,
                () => _connections.ConnectAsync(profile));
            return Task.CompletedTask;
        }

        return _connections.ConnectAsync(profile);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Status.DisconnectRequested -= RequestDisconnect;
        _connections.StatusChanged -= OnStatus;
        _diagnostics.Changed -= OnDiagnostics;
        _store.Changed -= OnProfilesChanged;
        _linkLog.Appended -= OnLinkLog;
        Simulators.Dispose();
    }

    /// <summary>Opening the SITL page looks for running simulators and checks what this machine has.</summary>
    protected override void OnPageChanged(ScreenPage? from)
    {
        if (SelectedPage?.Content == Simulators)
        {
            _ = Simulators.OpenAsync();
        }
    }

    private bool IsArmed => _vehicles.Active?.Snapshot.IsArmed == true;

    private void RequestDisconnect()
    {
        if (_connections.Status.State == LinkState.Connecting)
        {
            _connections.CancelConnect();
            return;
        }

        if (IsArmed)
        {
            Status.Confirm(Strings.LinkConfirmDisconnect, Strings.LinkConfirmDisconnectAction, _connections.DisconnectAsync);
            return;
        }

        _ = _connections.DisconnectAsync();
    }

    private void OnStatus(LinkStatus status) => _ui.Post(() => ApplyStatus(status));

    private void OnDiagnostics() => _ui.Post(ApplyDiagnostics);

    private void OnProfilesChanged() => _ui.Post(Links.Refresh);

    private void OnLinkLog(LinkLogEntry entry) => _ui.Post(() => Log.Append(entry.Text, entry.Severity, entry.TimeUtc));

    private void ApplyStatus(LinkStatus status)
    {
        Status.ApplyStatus(status, _connections.Simulators.Count > 0);
        Links.SetInUse(status.State == LinkState.Disconnected ? null : status.Profile?.Name);
        if (status.State != _lastState || status.Error is not null)
        {
            var name = status.Profile?.Name ?? string.Empty;
            switch (status.State)
            {
                case LinkState.Connecting when _lastState != LinkState.Connecting:
                    Log.Append(string.Format(CultureInfo.CurrentCulture, Strings.LinkLogConnecting, name));
                    break;
                case LinkState.Connected:
                    Log.Append(string.Format(CultureInfo.CurrentCulture, Strings.LinkLogConnected, name), NotificationSeverity.Success);
                    break;
                case LinkState.Lost:
                    Log.Append(string.Format(CultureInfo.CurrentCulture, Strings.LinkLogLost, name, status.Error), NotificationSeverity.Error);
                    break;
                case LinkState.Disconnected when status.Error is not null:
                    Log.Append(string.Format(CultureInfo.CurrentCulture, Strings.LinkLogFailed, status.Error), NotificationSeverity.Error);
                    break;
                case LinkState.Disconnected when _lastState != LinkState.Disconnected:
                    Log.Append(Strings.LinkLogDisconnected);
                    break;
            }
        }

        _lastState = status.State;
    }

    private void ApplyDiagnostics()
    {
        var steps = _diagnostics.Steps;
        Status.ApplySteps(_diagnostics.Phase, steps);
        Status.ApplyDetails(_diagnostics.Details);

        // A step that settles (done, warning, failed) goes to the log once.
        var settled = new Dictionary<string, LinkStepState>();
        foreach (var step in steps)
        {
            settled[step.Title] = step.State;
            if (step.State is LinkStepState.Done or LinkStepState.Warning or LinkStepState.Failed
                && (!_lastSteps.TryGetValue(step.Title, out var before) || before != step.State))
            {
                var level = step.State switch
                {
                    LinkStepState.Warning => NotificationSeverity.Warning,
                    LinkStepState.Failed => NotificationSeverity.Error,
                    _ => NotificationSeverity.Info
                };
                Log.Append(string.IsNullOrEmpty(step.Detail) ? step.Title : $"{step.Title}: {step.Detail}", level);
            }
        }

        _lastSteps = settled;
    }
}
