using System.Globalization;
using Ketras.Mavlink;
using Ketras.Mavlink.Messages;
using Ketras.Vehicles;
using Microsoft.Extensions.Hosting;

namespace Ketras.Comms;

/// <summary>Which steps a <see cref="LinkDiagnosticsWorkflow"/> reports.</summary>
public enum LinkPhase
{
    /// <summary>No link and no failed attempt to explain.</summary>
    Idle,

    /// <summary>A link is being opened.</summary>
    Connecting,

    /// <summary>The vehicle's heartbeats arrive.</summary>
    Connected,

    /// <summary>The heartbeats stopped; the link stays open.</summary>
    Lost,

    /// <summary>The last attempt failed; its steps say where.</summary>
    LastAttempt
}

/// <summary>How far one step has got.</summary>
public enum LinkStepState
{
    /// <summary>Not reached yet.</summary>
    Pending,

    /// <summary>Under way.</summary>
    Active,

    /// <summary>Done.</summary>
    Done,

    /// <summary>Under way or done, but something needs a look.</summary>
    Warning,

    /// <summary>Failed; the detail says why.</summary>
    Failed
}

/// <summary>One step of opening or keeping a link, in operator-facing English.</summary>
/// <param name="Title">What the step is, e.g. "Receive data".</param>
/// <param name="State">How far it has got.</param>
/// <param name="Detail">What was seen, e.g. "nothing for 8 s on udp://:14550".</param>
public sealed record LinkStep(string Title, LinkStepState State, string? Detail = null);

/// <summary>What is known about the open link and its vehicle; unknown values are null.</summary>
/// <param name="SinceHeartbeat">Time since the active vehicle's last heartbeat.</param>
/// <param name="SystemId">The vehicle's MAVLink system id.</param>
/// <param name="ComponentId">The autopilot's component id.</param>
/// <param name="Autopilot">Autopilot family, e.g. "PX4".</param>
/// <param name="VehicleType">Airframe class, e.g. "Fixed wing".</param>
/// <param name="Firmware">Firmware version.</param>
/// <param name="MessagesPerSecond">Messages received per second.</param>
/// <param name="BytesPerSecond">Bytes received per second.</param>
/// <param name="LossRatio">Lost share of packets since the link opened, 0…1.</param>
/// <param name="ChecksumErrors">Frames dropped for a bad checksum since the link opened.</param>
/// <param name="Radio">Latest telemetry radio report.</param>
/// <param name="RadioAge">Age of that report.</param>
public sealed record LinkDetails(
    TimeSpan? SinceHeartbeat,
    int? SystemId,
    int? ComponentId,
    string? Autopilot,
    string? VehicleType,
    string? Firmware,
    double? MessagesPerSecond,
    double? BytesPerSecond,
    double? LossRatio,
    long? ChecksumErrors,
    RadioReport? Radio,
    TimeSpan? RadioAge);

/// <summary>
/// Workflow: follows a link through its phases and says, step by step, how far it got: start the simulator (simulator
/// links), open the link, receive data, hear the vehicle's heartbeat, load its parameters. After a failed attempt
/// the steps stay as they ended. While a link is open it also refreshes <see cref="Details"/> once a second. It only
/// observes. Thread-safe; <see cref="Changed"/> is raised on any thread.
/// </summary>
public sealed class LinkDiagnosticsWorkflow : IHostedService, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly ConnectionService _connections;
    private readonly VehicleRegistry _vehicles;
    private readonly TimeProvider _time;
    private readonly TimeSpan _noDataWarning;
    private readonly object _lock = new();
    private ITimer? _timer;

    private LinkProfile? _profile;
    private string? _simulatorDetail;
    private MavlinkConnection? _connection;
    private DateTimeOffset _openedAt;
    private DateTimeOffset _lastDataAt;
    private long _lastBytes;
    private long _lastPackets;
    private DateTimeOffset _lastSample;
    private string? _failure;
    private bool _failedBeforeOpen;
    private (int Received, int Total)? _parameterProgress;
    private LinkState _lastState;

    private LinkPhase _phase;
    private IReadOnlyList<LinkStep> _steps = [];
    private LinkDetails? _details;

    /// <summary>Follows <paramref name="connections"/> and the vehicles on it.</summary>
    public LinkDiagnosticsWorkflow(ConnectionService connections, VehicleRegistry vehicles, TimeProvider time)
        : this(connections, vehicles, time, TimeSpan.FromSeconds(5))
    {
    }

    internal LinkDiagnosticsWorkflow(ConnectionService connections, VehicleRegistry vehicles, TimeProvider time, TimeSpan noDataWarning)
    {
        _connections = connections;
        _vehicles = vehicles;
        _time = time;
        _noDataWarning = noDataWarning;
    }

    /// <summary>Phase, steps or details changed.</summary>
    public event Action? Changed;

    /// <summary>Which steps <see cref="Steps"/> holds.</summary>
    public LinkPhase Phase
    {
        get
        {
            lock (_lock)
            {
                return _phase;
            }
        }
    }

    /// <summary>The steps of the current phase; empty when idle.</summary>
    public IReadOnlyList<LinkStep> Steps
    {
        get
        {
            lock (_lock)
            {
                return _steps;
            }
        }
    }

    /// <summary>The open link's details; null without one.</summary>
    public LinkDetails? Details
    {
        get
        {
            lock (_lock)
            {
                return _details;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _connections.StatusChanged += OnStatus;
        _connections.ConnectionOpened += OnOpened;
        _connections.ConnectionClosed += OnClosed;
        _vehicles.VehicleAdded += OnVehicleAdded;
        _timer = _time.CreateTimer(_ => Refresh(), null, Tick, Tick);
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
        _connections.StatusChanged -= OnStatus;
        _connections.ConnectionOpened -= OnOpened;
        _connections.ConnectionClosed -= OnClosed;
        _vehicles.VehicleAdded -= OnVehicleAdded;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnStatus(LinkStatus status)
    {
        lock (_lock)
        {
            if (status.State == LinkState.Connecting && status.Profile is { } profile && _lastState != LinkState.Connecting)
            {
                // A new attempt.
                _profile = profile;
                _connection = null;
                _failure = null;
                _failedBeforeOpen = false;
                _parameterProgress = null;
                _simulatorDetail = null;
            }

            _lastState = status.State;

            if (status.State == LinkState.Connecting && status.Detail is { } detail)
            {
                _simulatorDetail = detail;
            }

            if (status.State == LinkState.Disconnected)
            {
                _failure = status.Error;
                _failedBeforeOpen = status.Error is not null && _connection is null;
                if (status.Error is null)
                {
                    _profile = null;
                }
            }
        }

        Refresh();
    }

    private void OnOpened(MavlinkConnection connection)
    {
        lock (_lock)
        {
            _connection = connection;
            _openedAt = _lastDataAt = _lastSample = _time.GetUtcNow();
            _lastBytes = _lastPackets = 0;
        }

        Refresh();
    }

    private void OnClosed(MavlinkConnection connection)
    {
        lock (_lock)
        {
            // A connection that closes while its attempt is still connecting never opened: its steps end at "Open the link".
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }
        }

        Refresh();
    }

    private void OnVehicleAdded(Vehicle vehicle)
    {
        vehicle.Parameters.Progress += (received, total) =>
        {
            lock (_lock)
            {
                _parameterProgress = (received, total);
            }
        };
        Refresh();
    }

    private void Refresh()
    {
        var status = _connections.Status;
        var vehicle = _vehicles.Active;
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var statistics = _connection?.Statistics;
            if (statistics is not null)
            {
                Sample(statistics, now);
            }

            _phase = status.State switch
            {
                LinkState.Connecting => LinkPhase.Connecting,
                LinkState.Connected => LinkPhase.Connected,
                LinkState.Lost => LinkPhase.Lost,
                _ when _failure is not null && _profile is not null => LinkPhase.LastAttempt,
                _ => LinkPhase.Idle
            };
            _steps = _phase == LinkPhase.Idle || _profile is null ? [] : BuildSteps(_profile, statistics, vehicle, now);
            _details = _connection is not null && status.State != LinkState.Disconnected ? BuildDetails(statistics, vehicle, now) : null;
        }

        Changed?.Invoke();
    }

    private void Sample(LinkStatistics statistics, DateTimeOffset now)
    {
        if (statistics.BytesReceived > _lastBytes)
        {
            _lastDataAt = now;
        }
    }

    private List<LinkStep> BuildSteps(LinkProfile profile, LinkStatistics? statistics, Vehicle? vehicle, DateTimeOffset now)
    {
        var steps = new List<LinkStep>();
        var opened = _connection is not null || (_phase is LinkPhase.Connected or LinkPhase.Lost);
        if (profile.Kind == LinkKind.Simulator)
        {
            steps.Add(opened ? new LinkStep("Start the simulator", LinkStepState.Done, "running")
                : _failedBeforeOpen ? new LinkStep("Start the simulator", LinkStepState.Failed, _failure)
                : new LinkStep("Start the simulator", LinkStepState.Active, _simulatorDetail ?? "starting"));
        }

        var address = _connection?.Address ?? (profile.Kind == LinkKind.Simulator ? null : profile.Address);
        if (!opened)
        {
            var simulatorFailed = profile.Kind == LinkKind.Simulator && _failedBeforeOpen;
            steps.Add(_failedBeforeOpen && !simulatorFailed ? new LinkStep("Open the link", LinkStepState.Failed, _failure)
                : new LinkStep("Open the link", profile.Kind == LinkKind.Simulator ? LinkStepState.Pending : LinkStepState.Active, address));
            steps.Add(new LinkStep("Receive data", LinkStepState.Pending));
            steps.Add(new LinkStep("Vehicle heartbeat", LinkStepState.Pending));
            steps.Add(new LinkStep("Parameters", LinkStepState.Pending));
            return steps;
        }

        steps.Add(new LinkStep("Open the link", LinkStepState.Done, address));
        var bytes = statistics?.BytesReceived ?? 0;
        var silence = now - _lastDataAt;
        steps.Add(bytes == 0
            ? now - _openedAt >= _noDataWarning
                ? new LinkStep("Receive data", LinkStepState.Warning, NoDataHint(profile, now - _openedAt))
                : new LinkStep("Receive data", LinkStepState.Active, "waiting for the first bytes")
            : statistics is { PacketsReceived: 0, ChecksumErrors: > 0 }
                ? new LinkStep("Receive data", LinkStepState.Warning, "bytes arrive but no valid MAVLink frame: check the baud rate")
            : _phase == LinkPhase.Lost && silence >= _noDataWarning
                ? new LinkStep("Receive data", LinkStepState.Warning, Format("nothing for {0:F0} s", silence.TotalSeconds))
                : new LinkStep("Receive data", LinkStepState.Done, Format("{0:N0} bytes", bytes)));

        if (vehicle is null)
        {
            steps.Add(new LinkStep("Vehicle heartbeat", bytes > 0 ? LinkStepState.Active : LinkStepState.Pending, bytes > 0 ? "waiting for an autopilot's HEARTBEAT" : null));
            steps.Add(new LinkStep("Parameters", LinkStepState.Pending));
            return steps;
        }

        var identity = Format("system {0} · {1} · {2}", vehicle.Session.SystemId, AutopilotName(vehicle.Session.Autopilot), TypeName(vehicle.Session.VehicleType));
        steps.Add(_phase == LinkPhase.Lost
            ? new LinkStep("Vehicle heartbeat", LinkStepState.Failed, Format("last heard {0:F0} s ago ({1})", (now - vehicle.Session.LastHeartbeatUtc).TotalSeconds, identity))
            : new LinkStep("Vehicle heartbeat", LinkStepState.Done, identity));
        steps.Add(vehicle.Parameters.IsLoaded
            ? new LinkStep("Parameters", LinkStepState.Done, Format("{0} parameters", vehicle.Parameters.Values.Count))
            : new LinkStep("Parameters", LinkStepState.Active, _parameterProgress is { Total: > 0 } progress ? Format("{0} of {1}", progress.Received, progress.Total) : "requested"));
        return steps;
    }

    private LinkDetails BuildDetails(LinkStatistics? statistics, Vehicle? vehicle, DateTimeOffset now)
    {
        double? messages = null, bytes = null;
        if (statistics is not null)
        {
            var seconds = (now - _lastSample).TotalSeconds;
            if (seconds >= 0.5)
            {
                messages = (statistics.PacketsReceived - _lastPackets) / seconds;
                bytes = (statistics.BytesReceived - _lastBytes) / seconds;
                _lastPackets = statistics.PacketsReceived;
                _lastBytes = statistics.BytesReceived;
                _lastSample = now;
            }
            else
            {
                messages = _details?.MessagesPerSecond;
                bytes = _details?.BytesPerSecond;
            }
        }

        var session = vehicle?.Session;
        return new LinkDetails(
            session is null ? null : now - session.LastHeartbeatUtc,
            session?.SystemId,
            session?.ComponentId,
            session is null ? null : AutopilotName(session.Autopilot),
            session is null ? null : TypeName(session.VehicleType),
            session?.Status.Firmware?.Version,
            messages,
            bytes,
            statistics?.LossRatio,
            statistics?.ChecksumErrors,
            statistics?.Radio,
            statistics?.Radio is { } radio ? now - radio.ReceivedUtc : null);
    }

    private static string NoDataHint(LinkProfile profile, TimeSpan waited) => profile.Kind switch
    {
        LinkKind.Serial => Format("nothing for {0:F0} s on {1}: check the cable or radio and the baud rate", waited.TotalSeconds, profile.SerialPort ?? "the port"),
        LinkKind.Udp when string.IsNullOrWhiteSpace(profile.Host) => Format("nothing for {0:F0} s on port {1}: is the vehicle sending to this computer?", waited.TotalSeconds, profile.Port),
        _ => Format("nothing for {0:F0} s: is the vehicle up and the address right?", waited.TotalSeconds)
    };

    /// <summary>MAV_AUTOPILOT as the operator knows it.</summary>
    public static string AutopilotName(MavAutopilot autopilot) => autopilot switch
    {
        MavAutopilot.Px4 => "PX4",
        MavAutopilot.Ardupilotmega => "ArduPilot",
        MavAutopilot.Generic => "Generic",
        _ => Words(autopilot.ToString())
    };

    /// <summary>MAV_TYPE as the operator knows it, e.g. "Fixed wing".</summary>
    public static string TypeName(MavType type) => Words(type.ToString());

    /// <summary>"FixedWing" → "Fixed wing".</summary>
    private static string Words(string name)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ').Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                text.Append(name[i]);
            }
        }

        return text.ToString();
    }

    private static string Format(string format, params object?[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
}
