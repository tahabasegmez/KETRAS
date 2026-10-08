using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;
using Ketras.Station.Parameters;
using Ketras.Station.Vehicles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Events;

/// <summary>
/// Worker that interprets each vehicle's events (PX4 events protocol) once its events metadata is loaded: health and
/// arming check reports become <see cref="Vehicle.Health"/>, other events for the operator become
/// <see cref="Vehicle.Message"/>s. An event that asks for it (type <c>append_health_and_arming_messages</c>, e.g.
/// "Switching to mode 'Mission' is currently not possible") gets the failing checks of that mode appended. Events that arrive before the metadata wait for it. When the metadata is loaded the vehicle is
/// asked for a fresh report (MAV_CMD_RUN_PREARM_CHECKS), since PX4
/// otherwise reports only changes.
/// </summary>
public sealed class VehicleEventsWorker : IHostedService, IDisposable
{
    private const int WaitingCapacity = 200;

    private readonly VehicleRegistry _vehicles;
    private readonly MetadataService _metadata;
    private readonly ILogger<VehicleEventsWorker> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Vehicle, Follower> _followers = [];

    /// <summary>Creates the worker.</summary>
    public VehicleEventsWorker(VehicleRegistry vehicles, MetadataService metadata, ILogger<VehicleEventsWorker> logger)
    {
        _vehicles = vehicles;
        _metadata = metadata;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _vehicles.VehicleAdded += Follow;
        _vehicles.VehicleRemoved += Unfollow;
        _metadata.Loaded += OnMetadata;
        foreach (var vehicle in _vehicles.Vehicles)
        {
            Follow(vehicle);
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
        _metadata.Loaded -= OnMetadata;
        lock (_lock)
        {
            foreach (var follower in _followers.Values)
            {
                follower.Detach();
            }

            _followers.Clear();
        }
    }

    private void Follow(Vehicle vehicle)
    {
        Follower follower;
        lock (_lock)
        {
            if (_followers.ContainsKey(vehicle))
            {
                return;
            }

            follower = new Follower(vehicle, _logger);
            _followers[vehicle] = follower;
        }

        if (_metadata.For(vehicle.Id)?.Events is { } events)
        {
            follower.Start(events);
        }
    }

    private void Unfollow(Vehicle vehicle)
    {
        lock (_lock)
        {
            if (_followers.Remove(vehicle, out var follower))
            {
                follower.Detach();
            }
        }
    }

    private void OnMetadata(Model.Vehicles.VehicleId id, VehicleMetadata metadata)
    {
        Follower? follower;
        lock (_lock)
        {
            follower = _followers.Values.FirstOrDefault(f => f.Vehicle.Id == id);
        }

        if (follower is not null && metadata.Events is { } events)
        {
            follower.Start(events);
        }
    }

    /// <summary>One vehicle's events: waiting for the metadata, then interpreted in arrival order.</summary>
    private sealed class Follower
    {
        private readonly ILogger _logger;
        private readonly object _lock = new();
        private readonly Queue<VehicleEvent> _waiting = new();
        private readonly HealthReportAssembler _assembler = new();
        private EventDefinitions? _definitions;

        public Follower(Vehicle vehicle, ILogger logger)
        {
            Vehicle = vehicle;
            _logger = logger;
            vehicle.Session.Events.Received += OnEvent;
        }

        public Vehicle Vehicle { get; }

        public void Detach() => Vehicle.Session.Events.Received -= OnEvent;

        public void Start(EventDefinitions definitions)
        {
            lock (_lock)
            {
                if (_definitions is not null)
                {
                    return;
                }

                _definitions = definitions;
                Vehicle.EventDefinitions = definitions;
                while (_waiting.TryDequeue(out var waiting))
                {
                    Interpret(waiting);
                }
            }

            _ = RequestReportAsync();
        }

        private void OnEvent(VehicleEvent @event)
        {
            lock (_lock)
            {
                if (_definitions is null)
                {
                    _waiting.Enqueue(@event);
                    while (_waiting.Count > WaitingCapacity)
                    {
                        _waiting.Dequeue();
                    }

                    return;
                }

                Interpret(@event);
            }
        }

        private void Interpret(VehicleEvent raw)
        {
            if (_definitions!.Interpret(raw) is not { } @event)
            {
                _logger.LogDebug("Vehicle {Vehicle}: event {Id:x8} is not in the metadata", Vehicle.Id, raw.Id);
                return;
            }

            if (_assembler.Add(@event) is { } report)
            {
                Vehicle.SetHealth(report);
                return;
            }

            if (@event.Group is "arming_check" or "health" || !@event.IsForDisplay)
            {
                return;
            }

            var text = @event.Message;
            if (@event.Type == "append_health_and_arming_messages" && @event.Values.Count > 0
                && _definitions.ModeGroupOf((uint)@event.Values[0].Raw) is { } group
                && Vehicle.Health?.ChecksFor(group) is { Count: > 0 } checks)
            {
                text += ": " + string.Join("; ", checks.Select(check => check.Message));
            }

            Vehicle.RaiseMessage(@event.Severity, text);
        }

        private async Task RequestReportAsync()
        {
            try
            {
                var result = await Vehicle.Session.Commands.SendLongAsync(MavCmd.RunPrearmChecks);
                if (!result.IsAccepted)
                {
                    _logger.LogInformation("Vehicle {Vehicle}: health report request {Outcome} {Reason}", Vehicle.Id, result.Outcome, result.Reason);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogDebug(exception, "Health report request not sent");
            }
        }
    }
}
