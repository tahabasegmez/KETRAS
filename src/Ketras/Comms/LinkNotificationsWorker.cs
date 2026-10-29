using Ketras.Events;
using Ketras.Notifications;
using Ketras.Simulation;
using Ketras.Vehicles;
using Microsoft.Extensions.Hosting;

namespace Ketras.Comms;

/// <summary>
/// Worker that brings what the vehicle says to the operator: every vehicle message (STATUSTEXT and events) into the
/// <see cref="LinkLogStore"/>, its warnings and errors also as notifications, the same text at most once per
/// <see cref="RepeatWindow"/>; while the disarmed vehicle cannot arm in its current mode, a "Preflight checks"
/// condition listing what fails; a link that gets no usable data as a condition while it lasts; and a simulator that
/// comes up, fails to start or stops on its own as events. Connected, lost and restored links are reported by
/// <see cref="ConnectionService"/> itself.
/// </summary>
public sealed class LinkNotificationsWorker : IHostedService, IDisposable
{
    /// <summary>The same vehicle message is not repeated within this time.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);

    private const string LinkSource = "Link";
    private const string StepCondition = "step:";
    private const string PreflightCondition = "preflight";

    private readonly ConnectionService _connections;
    private readonly VehicleRegistry _vehicles;
    private readonly LinkDiagnosticsWorkflow _diagnostics;
    private readonly NotificationStore _notifications;
    private readonly LinkLogStore _log;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<Vehicle, Followed> _followed = [];
    private readonly Dictionary<(Vehicle, string), DateTimeOffset> _lastSaid = [];
    private readonly Dictionary<ISimulator, Action<SimulatorStatus>> _simulatorHandlers = [];
    private readonly Dictionary<ISimulator, (SimulatorRunState State, bool Reached)> _simulatorStates = [];
    private HashSet<string> _stepConditions = [];

    /// <summary>Creates the worker.</summary>
    public LinkNotificationsWorker(ConnectionService connections, VehicleRegistry vehicles, LinkDiagnosticsWorkflow diagnostics,
        NotificationStore notifications, LinkLogStore log, TimeProvider time)
    {
        _connections = connections;
        _vehicles = vehicles;
        _diagnostics = diagnostics;
        _notifications = notifications;
        _log = log;
        _time = time;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _vehicles.VehicleAdded += Follow;
        _vehicles.VehicleRemoved += Unfollow;
        foreach (var vehicle in _vehicles.Vehicles)
        {
            Follow(vehicle);
        }

        _diagnostics.Changed += OnDiagnostics;
        foreach (var simulator in _connections.Simulators)
        {
            Action<SimulatorStatus> handler = status => OnSimulator(simulator, status);
            lock (_lock)
            {
                _simulatorStates[simulator] = (simulator.Status.State, simulator.Status.State == SimulatorRunState.Running);
                _simulatorHandlers[simulator] = handler;
            }

            simulator.StatusChanged += handler;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _vehicles.VehicleAdded -= Follow;
        _vehicles.VehicleRemoved -= Unfollow;
        _diagnostics.Changed -= OnDiagnostics;
        lock (_lock)
        {
            foreach (var (vehicle, followed) in _followed)
            {
                followed.Detach(vehicle);
            }

            foreach (var (simulator, handler) in _simulatorHandlers)
            {
                simulator.StatusChanged -= handler;
            }

            _followed.Clear();
            _simulatorHandlers.Clear();
        }
    }

    private void Follow(Vehicle vehicle)
    {
        var followed = new Followed(
            (severity, text) => OnMessage(vehicle, severity, text),
            _ => UpdatePreflight(vehicle),
            snapshot => OnSnapshot(vehicle, snapshot));
        lock (_lock)
        {
            if (!_followed.TryAdd(vehicle, followed))
            {
                return;
            }
        }

        vehicle.Message += followed.Message;
        vehicle.HealthChanged += followed.Health;
        vehicle.SnapshotChanged += followed.Snapshot;
    }

    private void Unfollow(Vehicle vehicle)
    {
        lock (_lock)
        {
            if (_followed.Remove(vehicle, out var followed))
            {
                followed.Detach(vehicle);
            }

            foreach (var key in _lastSaid.Keys.Where(key => key.Item1 == vehicle).ToList())
            {
                _lastSaid.Remove(key);
            }
        }

        _notifications.ClearCondition(vehicle.Name, PreflightCondition);
    }

    private void OnMessage(Vehicle vehicle, NotificationSeverity severity, string text)
    {
        _log.Append(severity, $"{vehicle.Name}: {text}");
        if (severity < NotificationSeverity.Warning)
        {
            return;
        }

        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_lastSaid.TryGetValue((vehicle, text), out var said) && now - said < RepeatWindow)
            {
                return;
            }

            _lastSaid[(vehicle, text)] = now;
        }

        _notifications.Notify(vehicle.Name, severity, text);
    }

    /// <summary>Snapshots come ten times a second; the condition changes only with the mode or arming.</summary>
    private void OnSnapshot(Vehicle vehicle, VehicleSnapshot snapshot)
    {
        Followed? followed;
        lock (_lock)
        {
            if (!_followed.TryGetValue(vehicle, out followed) || (followed.Mode == snapshot.Mode && followed.IsArmed == snapshot.IsArmed))
            {
                return;
            }

            followed.Mode = snapshot.Mode;
            followed.IsArmed = snapshot.IsArmed;
        }

        UpdatePreflight(vehicle);
    }

    /// <summary>While the disarmed vehicle cannot arm in its mode, a condition names the checks that fail there.</summary>
    private void UpdatePreflight(Vehicle vehicle)
    {
        var snapshot = vehicle.Snapshot;
        var group = vehicle.ModeGroupOf(snapshot.Mode);
        if (vehicle.Health is { } health && group is { } g && !snapshot.IsArmed && snapshot.IsConnected && !health.CanArm(g))
        {
            var failing = health.ChecksFor(g).Where(check => check.Severity == NotificationSeverity.Error).ToList();
            _notifications.SetCondition(vehicle.Name, PreflightCondition, NotificationSeverity.Warning, "Preflight checks: the vehicle cannot arm",
                failing.Count > 0 ? string.Join('\n', failing.Select(check => check.Message)) : null);
            return;
        }

        _notifications.ClearCondition(vehicle.Name, PreflightCondition);
    }

    /// <summary>A step that needs a look (e.g. no data arriving) holds as a condition until it settles.</summary>
    private void OnDiagnostics()
    {
        var phase = _diagnostics.Phase;
        var warnings = phase is LinkPhase.Connecting or LinkPhase.Connected
            ? _diagnostics.Steps.Where(step => step.State == LinkStepState.Warning).ToList()
            : [];
        var keys = warnings.Select(step => StepCondition + step.Title).ToHashSet();
        HashSet<string> ended;
        lock (_lock)
        {
            ended = [.. _stepConditions.Except(keys)];
            _stepConditions = keys;
        }

        foreach (var step in warnings)
        {
            var title = step.Title == "Receive data" ? "No usable data on the link" : step.Title;
            _notifications.SetCondition(LinkSource, StepCondition + step.Title, NotificationSeverity.Warning, title, step.Detail);
        }

        foreach (var key in ended)
        {
            _notifications.ClearCondition(LinkSource, key);
        }
    }

    private void OnSimulator(ISimulator simulator, SimulatorStatus status)
    {
        var name = simulator.Description.Name;
        (SimulatorRunState State, bool Reached) before;
        lock (_lock)
        {
            before = _simulatorStates.GetValueOrDefault(simulator);
            var reached = status.State switch
            {
                SimulatorRunState.Running => true,
                SimulatorRunState.Starting => false,
                _ => before.Reached
            };
            _simulatorStates[simulator] = (status.State, reached);
        }

        if (status.State == before.State && status.State != SimulatorRunState.Stopped)
        {
            return;
        }

        switch (status.State)
        {
            case SimulatorRunState.Running when before.State != SimulatorRunState.Running:
                _notifications.Notify(name, before.State == SimulatorRunState.Starting ? NotificationSeverity.Success : NotificationSeverity.Info,
                    $"{name} is running", status.Detail);
                break;
            case SimulatorRunState.Stopped when status.Error is { } error && before.State != SimulatorRunState.Stopped:
                _notifications.Notify(name, NotificationSeverity.Error, before.Reached ? $"{name} stopped" : $"{name} did not start", error);
                break;
        }
    }

    /// <summary>The handlers on one vehicle and the mode and arming the preflight condition was last checked with.</summary>
    private sealed class Followed(Action<NotificationSeverity, string> message, Action<HealthReport> health, Action<VehicleSnapshot> snapshot)
    {
        public Action<NotificationSeverity, string> Message { get; } = message;

        public Action<HealthReport> Health { get; } = health;

        public Action<VehicleSnapshot> Snapshot { get; } = snapshot;

        public string? Mode { get; set; }

        public bool IsArmed { get; set; }

        public void Detach(Vehicle vehicle)
        {
            vehicle.Message -= Message;
            vehicle.HealthChanged -= Health;
            vehicle.SnapshotChanged -= Snapshot;
        }
    }
}
