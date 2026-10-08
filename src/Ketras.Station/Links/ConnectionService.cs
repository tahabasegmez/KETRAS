using System.Globalization;
using Ketras.Mavlink;
using Ketras.Mavlink.Logging;
using Ketras.Station.Notifications;
using Ketras.Station.Settings;
using Ketras.Station.Simulation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ketras.Station.Links;

/// <summary>Strategy: turns a profile into a MAVLink connection; tests replace it to reach a fake vehicle.</summary>
public interface ILinkFactory
{
    /// <summary>A connection for <paramref name="profile"/> (UDP, TCP or serial); nothing opens until it is started.</summary>
    MavlinkConnection Create(LinkProfile profile, MavlinkConnectionOptions options);
}

/// <summary>Opens UDP, TCP and serial links by their <see cref="LinkProfile.Address"/>.</summary>
public sealed class MavlinkLinkFactory(ILoggerFactory loggers) : ILinkFactory
{
    /// <inheritdoc />
    public MavlinkConnection Create(LinkProfile profile, MavlinkConnectionOptions options) =>
        MavlinkConnection.Create(profile.Address, options, loggers.CreateLogger("Ketras.Mavlink"));
}

/// <summary>
/// Opens, closes and switches the vehicle link (one at a time) on the operator's request; nothing connects on its own.
/// A simulator profile starts its simulator first when it is not running. Every session is recorded as a .tlog. Events
/// are raised on any thread.
/// </summary>
public sealed class ConnectionService : IAsyncDisposable
{
    private const string Source = "Link";
    private const string LostCondition = "lost";

    private readonly ILinkFactory _factory;
    private readonly IReadOnlyList<ISimulator> _simulators;
    private readonly LinkProfileStore _profiles;
    private readonly NotificationStore _notifications;
    private readonly StationOptions _options;
    private readonly StationPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private CancellationTokenSource? _attempt;
    private MavlinkConnection? _connection;
    private TlogWriter? _tlog;
    private LinkStatus _status = LinkStatus.Disconnected;

    /// <summary>Creates the service; <paramref name="simulators"/> are the ones simulator profiles may start.</summary>
    public ConnectionService(ILinkFactory factory, IEnumerable<ISimulator> simulators, LinkProfileStore profiles, NotificationStore notifications,
        IOptions<StationOptions> options, StationPaths paths, TimeProvider time, ILogger<ConnectionService> logger)
    {
        _factory = factory;
        _simulators = [.. simulators];
        _profiles = profiles;
        _notifications = notifications;
        _options = options.Value;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    /// <summary>The link's state.</summary>
    public LinkStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>The open connection; null while disconnected or still starting a simulator.</summary>
    public MavlinkConnection? Connection
    {
        get
        {
            lock (_lock)
            {
                return _connection;
            }
        }
    }

    /// <summary>The simulators simulator profiles can start.</summary>
    public IReadOnlyList<ISimulator> Simulators => _simulators;

    /// <summary><see cref="Status"/> changed.</summary>
    public event Action<LinkStatus>? StatusChanged;

    /// <summary>A connection was created and is about to start: subscribe to its vehicles here.</summary>
    public event Action<MavlinkConnection>? ConnectionOpened;

    /// <summary>A connection closed (disconnect, switch or a failed start).</summary>
    public event Action<MavlinkConnection>? ConnectionClosed;

    /// <summary>
    /// Opens <paramref name="profile"/>, closing the current link first. A failure is reported through
    /// <see cref="Status"/> and the notifications, never thrown; the result says whether the link opened.
    /// </summary>
    public async Task<bool> ConnectAsync(LinkProfile profile, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            await CloseAsync(announce: false);
            _profiles.MarkUsed(profile.Name);
            var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lock (_lock)
            {
                _attempt = attempt;
            }

            SetStatus(new LinkStatus(LinkState.Connecting, profile));
            try
            {
                var transport = profile.Kind == LinkKind.Simulator ? await StartSimulatorAsync(profile, attempt.Token) : profile;
                return await OpenAsync(profile, transport, attempt.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus(LinkStatus.Disconnected);
                return false;
            }
            catch (LinkException exception)
            {
                Fail(profile, exception.Message);
                return false;
            }
        }
        finally
        {
            lock (_lock)
            {
                _attempt = null;
            }

            _gate.Release();
        }
    }

    /// <summary>Gives up a connection attempt in progress (e.g. waiting for a simulator); the simulator keeps starting.</summary>
    public void CancelConnect()
    {
        lock (_lock)
        {
            _attempt?.Cancel();
        }
    }

    /// <summary>Closes the link.</summary>
    public async Task DisconnectAsync()
    {
        CancelConnect();
        await _gate.WaitAsync();
        try
        {
            await CloseAsync(announce: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LinkProfile> StartSimulatorAsync(LinkProfile profile, CancellationToken cancellation)
    {
        var simulator = _simulators.FirstOrDefault(s => s.Description.Id == profile.SimulatorId)
            ?? throw new LinkException($"This station has no simulator '{profile.SimulatorId}'.");

        if (simulator.Status.State == SimulatorRunState.Running && simulator.Link is { } running)
        {
            return Adopt(running, profile);
        }

        var ready = new TaskCompletionSource<LinkProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(SimulatorStatus status)
        {
            switch (status.State)
            {
                case SimulatorRunState.Running when simulator.Link is { } link:
                    ready.TrySetResult(link);
                    break;
                case SimulatorRunState.Stopped:
                    ready.TrySetException(new LinkException(status.Error ?? $"{simulator.Description.Name} stopped before it was ready."));
                    break;
                default:
                    SetStatus(new LinkStatus(LinkState.Connecting, profile, Detail: $"{simulator.Description.Name}: {status.Detail ?? "starting"}"));
                    break;
            }
        }

        simulator.StatusChanged += OnStatus;
        try
        {
            if (simulator.Status.State == SimulatorRunState.Stopped)
            {
                var description = simulator.Description;
                try
                {
                    await simulator.StartAsync(new SimulatorLaunch(description.Worlds[0].Id, description.DefaultHome), cancellation);
                }
                catch (InvalidOperationException exception)
                {
                    throw new LinkException(exception.Message);
                }
            }

            OnStatus(simulator.Status);
            return Adopt(await ready.Task.WaitAsync(cancellation), profile);
        }
        finally
        {
            simulator.StatusChanged -= OnStatus;
        }
    }

    /// <summary>The simulator's link under the profile's name; "auto" keeps the autopilot the simulator knows it runs.</summary>
    private static LinkProfile Adopt(LinkProfile link, LinkProfile profile) => link with
    {
        Name = profile.Name,
        Autopilot = profile.Autopilot == "auto" ? link.Autopilot : profile.Autopilot,
        TargetSystemId = profile.TargetSystemId
    };

    private async Task<bool> OpenAsync(LinkProfile profile, LinkProfile transport, CancellationToken cancellation)
    {
        MavlinkConnection connection;
        try
        {
            connection = _factory.Create(transport, new MavlinkConnectionOptions
            {
                SystemId = _options.Mavlink.SystemId,
                ComponentId = _options.Mavlink.ComponentId,
                TargetSystemId = profile.TargetSystemId,
                AutopilotProfile = profile.Autopilot,
                LinkTimeout = _options.Mavlink.LinkTimeout
            });
        }
        catch (FormatException exception)
        {
            throw new LinkException(exception.Message);
        }

        connection.VehicleDiscovered += vehicle => vehicle.ConnectionChanged += _ => Reassess(connection);
        connection.VehicleDiscovered += _ => Reassess(connection);
        connection.Faulted += exception => Reassess(connection, exception.Message);
        lock (_lock)
        {
            _connection = connection;
        }

        ConnectionOpened?.Invoke(connection);
        try
        {
            await connection.StartAsync(cancellation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_lock)
            {
                _connection = null;
            }

            await connection.DisposeAsync();
            ConnectionClosed?.Invoke(connection);
            throw new LinkException(OpenFailure(transport, exception));
        }

        StartTlog(connection, profile);
        _logger.LogInformation("Link {Name} open on {Address}", profile.Name, connection.Address);
        Reassess(connection);
        return true;
    }

    /// <summary>The link's state follows its vehicles: any connected → Connected; heard but silent → Lost.</summary>
    private void Reassess(MavlinkConnection connection, string? fault = null)
    {
        LinkStatus previous, next;
        lock (_lock)
        {
            if (!ReferenceEquals(connection, _connection) || _status.Profile is not { } profile)
            {
                return;
            }

            previous = _status;
            var vehicles = connection.Vehicles;
            var error = fault ?? connection.Fault?.Message;
            next = error is not null ? new LinkStatus(LinkState.Lost, profile, error)
                : vehicles.Any(v => v.IsConnected) ? new LinkStatus(LinkState.Connected, profile)
                : vehicles.Count > 0 ? new LinkStatus(LinkState.Lost, profile, "No heartbeat from the vehicle.")
                : new LinkStatus(LinkState.Connecting, profile);
            if (next == previous)
            {
                return;
            }

            _status = next;
        }

        StatusChanged?.Invoke(next);
        switch (next.State)
        {
            case LinkState.Connected when previous.State == LinkState.Lost:
                _notifications.ClearCondition(Source, LostCondition);
                _notifications.Notify(Source, NotificationSeverity.Success, "Vehicle link restored", next.Profile?.Name);
                break;
            case LinkState.Connected:
                _notifications.Notify(Source, NotificationSeverity.Success, "Vehicle connected", next.Profile?.Name);
                break;
            case LinkState.Lost when previous.State != LinkState.Lost:
                _notifications.Notify(Source, NotificationSeverity.Error, "Vehicle link lost", next.Error);
                _notifications.SetCondition(Source, LostCondition, NotificationSeverity.Error, "No vehicle link", next.Error);
                break;
        }
    }

    private void StartTlog(MavlinkConnection connection, LinkProfile profile)
    {
        if (!_options.TelemetryLog.Enabled)
        {
            return;
        }

        var directory = _paths.DataPath(_options.TelemetryLog.Directory, "tlogs");
        var name = string.Create(CultureInfo.InvariantCulture, $"{_time.GetLocalNow():yyyy-MM-dd_HH-mm-ss}_{Sanitize(profile.Name)}.tlog");
        try
        {
            var writer = TlogWriter.Create(Path.Combine(directory, name));
            lock (_lock)
            {
                _tlog = writer;
            }

            connection.RecordingFailed += exception =>
                _notifications.Notify(Source, NotificationSeverity.Warning, "Telemetry log stopped", exception.Message);
            connection.Recorder = writer;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Telemetry log could not be created in {Directory}", directory);
            _notifications.Notify(Source, NotificationSeverity.Warning, "Telemetry log not recorded", exception.Message);
        }
    }

    private async Task CloseAsync(bool announce)
    {
        MavlinkConnection? connection;
        TlogWriter? tlog;
        LinkProfile? profile;
        lock (_lock)
        {
            connection = _connection;
            tlog = _tlog;
            profile = _status.Profile;
            _connection = null;
            _tlog = null;
        }

        if (connection is not null)
        {
            connection.Recorder = null;
            await connection.DisposeAsync();
            tlog?.Dispose();
            ConnectionClosed?.Invoke(connection);
            _notifications.ClearCondition(Source, LostCondition);
            _logger.LogInformation("Link {Name} closed", profile?.Name);
            if (announce)
            {
                _notifications.Notify(Source, NotificationSeverity.Info, "Disconnected", profile?.Name);
            }
        }

        SetStatus(LinkStatus.Disconnected);
    }

    private void Fail(LinkProfile profile, string reason)
    {
        _logger.LogWarning("Link {Name} not opened: {Reason}", profile.Name, reason);
        SetStatus(new LinkStatus(LinkState.Disconnected, null, reason));
        _notifications.Notify(Source, NotificationSeverity.Error, "Not connected", reason);
    }

    private void SetStatus(LinkStatus status)
    {
        lock (_lock)
        {
            if (status == _status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>Operator-facing reason a transport did not open; Windows reports a missing COM port as a missing file.</summary>
    private static string OpenFailure(LinkProfile profile, Exception exception) => exception switch
    {
        FileNotFoundException or IOException when profile.Kind == LinkKind.Serial && profile.SerialPort is { } port
            && exception.Message.Contains(port, StringComparison.OrdinalIgnoreCase)
            => $"Serial port {port} was not found. Check the cable or radio and rescan the ports.",
        UnauthorizedAccessException when profile.Kind == LinkKind.Serial => $"Serial port {profile.SerialPort} is in use by another program.",
        _ => exception.Message
    };

    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private sealed class LinkException(string message) : Exception(message);
}
