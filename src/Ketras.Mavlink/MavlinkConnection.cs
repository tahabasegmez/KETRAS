using System.Collections.Concurrent;
using System.Threading.Channels;
using Ketras.Mavlink.Autopilots;
using Ketras.Mavlink.Framing;
using Ketras.Mavlink.Logging;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Transport;
using Ketras.Mavlink.Vehicles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.Mavlink;

/// <summary>Who this ground station is and which vehicles it follows.</summary>
public sealed class MavlinkConnectionOptions
{
    /// <summary>Own system id; 255 is the conventional ground-station id.</summary>
    public byte SystemId { get; init; } = 255;

    /// <summary>Own component id; 190 is MAV_COMP_ID_MISSIONPLANNER.</summary>
    public byte ComponentId { get; init; } = 190;

    /// <summary>Follow only this system id; null follows every autopilot heard.</summary>
    public byte? TargetSystemId { get; init; }

    /// <summary>"auto" (from the HEARTBEAT) or a profile name such as "px4".</summary>
    public string AutopilotProfile { get; init; } = "auto";

    /// <summary>Interval of this station's own HEARTBEAT.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Silence after which a vehicle counts as disconnected.</summary>
    public TimeSpan LinkTimeout { get; init; } = TimeSpan.FromSeconds(3.5);
}

/// <summary>A HEARTBEAT seen on a link.</summary>
/// <param name="SystemId">Sender's system id.</param>
/// <param name="ComponentId">Sender's component id.</param>
/// <param name="Type">What the sender is (MAV_TYPE).</param>
/// <param name="Autopilot">The sender's autopilot (MAV_AUTOPILOT); invalid for a non-autopilot component.</param>
public sealed record MavlinkHeartbeat(byte SystemId, byte ComponentId, MavType Type, MavAutopilot Autopilot);

/// <summary>A valid frame seen on a link: who sent it and which message it carried.</summary>
/// <param name="SystemId">Sender's system id.</param>
/// <param name="ComponentId">Sender's component id.</param>
/// <param name="MessageId">MAVLink message id.</param>
public readonly record struct MavlinkFrameInfo(byte SystemId, byte ComponentId, uint MessageId);

/// <summary>
/// Facade over one MAVLink link: framing, this station's GCS heartbeat, a session per vehicle, the link watchdog and
/// telemetry logging. The transport's thread only parses; every event is raised on the connection's single dispatch
/// thread, one subscriber at a time, so a slow or failing subscriber never stops the link. Subscribers must not block.
/// </summary>
public sealed class MavlinkConnection : IAsyncDisposable
{
    private const int DispatchCapacity = 8192;
    private const uint SetupSigningMessageId = 256;
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(250);

    private readonly IMavlinkTransport _transport;
    private readonly MavlinkConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly MavlinkParser _parser = new();
    private readonly object _parseLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<object> _dispatch;
    private readonly ConcurrentDictionary<byte, VehicleSession> _vehicles = new();
    private readonly Dictionary<(byte System, byte Component), byte> _lastSequence = new();
    private readonly List<Task> _loops = [];
    private byte _sequence;
    private long _bytesReceived;
    private long _packetsLost;
    private long _packetsDropped;
    private long _sendErrors;
    private bool _sendFailing;
    private RadioReport? _radio;
    private volatile TlogWriter? _recorder;
    private volatile Exception? _fault;

    private MavlinkConnection(IMavlinkTransport transport, MavlinkConnectionOptions? options, ILogger? logger)
    {
        _transport = transport;
        _options = options ?? new MavlinkConnectionOptions();
        _logger = logger ?? NullLogger.Instance;
        _dispatch = Channel.CreateBounded<object>(
            new BoundedChannelOptions(DispatchCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _packetsDropped));
        _transport.Received += OnReceived;
        _transport.Faulted += exception => _dispatch.Writer.TryWrite(new TransportFault(exception));
    }

    /// <summary>
    /// Creates a connection to an address; nothing opens until <see cref="StartAsync"/>. Addresses:
    /// <c>udp://:14550</c> (listen), <c>udp://host:port</c> (fixed peer), <c>tcp://host:port</c>, <c>serial:COM5:57600</c>
    /// or <c>serial:/dev/ttyUSB0:57600</c>. Throws <see cref="FormatException"/> for anything else.
    /// </summary>
    public static MavlinkConnection Create(string address, MavlinkConnectionOptions? options = null, ILogger? logger = null) =>
        new(MavlinkEndpoint.Create(address), options, logger);

    /// <summary>Creates a connection over a custom transport; nothing happens until <see cref="StartAsync"/>.</summary>
    public static MavlinkConnection Create(IMavlinkTransport transport, MavlinkConnectionOptions? options = null, ILogger? logger = null) =>
        new(transport, options, logger);

    /// <summary>The transport's address.</summary>
    public string Address => _transport.Description;

    /// <summary>Every vehicle heard on this link, by system id. Safe from any thread.</summary>
    public IReadOnlyList<VehicleSession> Vehicles => _vehicles.Values.OrderBy(v => v.SystemId).ToList();

    /// <summary>The transport's failure, once it stopped receiving; null while it works.</summary>
    public Exception? Fault => _fault;

    /// <summary>Counters since the link opened. Safe from any thread.</summary>
    public LinkStatistics Statistics
    {
        get
        {
            lock (_parseLock)
            {
                return new LinkStatistics(_bytesReceived, _parser.PacketsReceived, _packetsLost, _parser.ChecksumErrors, _radio,
                    Interlocked.Read(ref _packetsDropped), Interlocked.Read(ref _sendErrors));
            }
        }
    }

    /// <summary>
    /// Where frames in both directions are recorded; null records nothing. The connection does not own the writer: the
    /// caller disposes it after detaching it. A write failure detaches it and raises <see cref="RecordingFailed"/>.
    /// </summary>
    public TlogWriter? Recorder
    {
        get => _recorder;
        set => _recorder = value;
    }

    /// <summary>A vehicle (an autopilot system id) was heard for the first time.</summary>
    public event Action<VehicleSession>? VehicleDiscovered;

    /// <summary>A valid frame arrived from any system.</summary>
    public event Action<MavlinkFrameInfo>? FrameReceived;

    /// <summary>A HEARTBEAT arrived from any system or component.</summary>
    public event Action<MavlinkHeartbeat>? HeartbeatReceived;

    /// <summary>An ADS-B report arrived from any system.</summary>
    public event Action<AdsbTraffic>? TrafficReceived;

    /// <summary>The transport stopped receiving; every vehicle on the link is marked as lost.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>A send failed after earlier sends had worked (e.g. the network went away); raised once per failure run.</summary>
    public event Action<Exception>? SendFailed;

    /// <summary>The recorder could not be written and was detached.</summary>
    public event Action<Exception>? RecordingFailed;

    /// <summary>Opens the transport and starts the heartbeat, the watchdog and event dispatch.</summary>
    public async Task StartAsync(CancellationToken cancellation = default)
    {
        await _transport.OpenAsync(cancellation);
        _loops.Add(Task.Run(DispatchLoopAsync, CancellationToken.None));
        _loops.Add(Task.Run(HeartbeatLoopAsync, CancellationToken.None));
        _loops.Add(Task.Run(WatchdogLoopAsync, CancellationToken.None));
    }

    /// <summary>
    /// Encodes and sends a message as this station. Safe from any thread. A transport failure is counted and reported
    /// through <see cref="SendFailed"/>, never thrown: the protocol clients then see no response, as on a lossy link.
    /// </summary>
    internal async ValueTask SendAsync(IMavlinkMessage message)
    {
        byte[] frame;
        await _sendLock.WaitAsync();
        try
        {
            frame = MavlinkFrame.Encode(message, _sequence++, _options.SystemId, _options.ComponentId);
            await _transport.SendAsync(frame);
            _sendFailing = false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _sendErrors);
            if (!_sendFailing)
            {
                _sendFailing = true;
                _logger.LogWarning(exception, "Send on {Address} failed", Address);
                _dispatch.Writer.TryWrite(new SendFailure(exception));
            }

            return;
        }
        finally
        {
            _sendLock.Release();
        }

        if (message.WireId != SetupSigningMessageId)
        {
            Record(frame);
        }
    }

    /// <summary>Transport thread: parse, count and queue; nothing else runs here.</summary>
    private void OnReceived(ReadOnlyMemory<byte> data)
    {
        IReadOnlyList<MavlinkPacket> packets;
        lock (_parseLock)
        {
            _bytesReceived += data.Length;
            packets = _parser.Feed(data.Span);
            foreach (var packet in packets)
            {
                Count(packet);
            }
        }

        foreach (var packet in packets)
        {
            _dispatch.Writer.TryWrite(packet);
        }
    }

    /// <summary>Sequence gaps per sender; a jump of more than half the range is a restart or reorder, not loss.</summary>
    private void Count(MavlinkPacket packet)
    {
        var key = (packet.SystemId, packet.ComponentId);
        if (_lastSequence.TryGetValue(key, out var last))
        {
            var gap = (byte)(packet.Sequence - last - 1);
            if (gap is > 0 and < 128)
            {
                _packetsLost += gap;
            }
        }

        _lastSequence[key] = packet.Sequence;
        if (packet.Message is RadioStatusMessage radio)
        {
            _radio = new RadioReport(radio.Rssi, radio.Remrssi, radio.Noise, radio.Remnoise, radio.Txbuf, radio.Rxerrors, radio.Fixed, DateTime.UtcNow);
        }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var item in _dispatch.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    switch (item)
                    {
                        case MavlinkPacket packet:
                            Route(packet);
                            break;
                        case WatchdogTick tick:
                            foreach (var vehicle in _vehicles.Values)
                            {
                                vehicle.CheckLink(_options.LinkTimeout, tick.NowUtc);
                            }

                            break;
                        case TransportFault fault:
                            _fault = fault.Exception;
                            _logger.LogError(fault.Exception, "Transport {Address} stopped", Address);
                            foreach (var vehicle in _vehicles.Values)
                            {
                                vehicle.MarkLost();
                            }

                            Subscribers.Raise(Faulted, fault.Exception, _logger, nameof(Faulted));
                            break;
                        case SendFailure failure:
                            Subscribers.Raise(SendFailed, failure.Exception, _logger, nameof(SendFailed));
                            break;
                        case RecordingFailure failure:
                            Subscribers.Raise(RecordingFailed, failure.Exception, _logger, nameof(RecordingFailed));
                            break;
                    }
                }
                catch (Exception exception)
                {
                    // Subscribers are isolated already; this guards the routing itself, so one bad frame never ends dispatch.
                    _logger.LogError(exception, "Dispatch on {Address} failed for {Item}", Address, item.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private void Route(MavlinkPacket packet)
    {
        if (packet.MessageId != SetupSigningMessageId)
        {
            Record(MavlinkFrame.Unsigned(packet));
        }

        if (packet.Message is HeartbeatMessage heartbeat)
        {
            if (!_vehicles.ContainsKey(packet.SystemId) && IsFollowedAutopilot(packet, heartbeat))
            {
                Discover(packet, heartbeat);
            }

            Subscribers.Raise(HeartbeatReceived,
                new MavlinkHeartbeat(packet.SystemId, packet.ComponentId, (MavType)heartbeat.Type, (MavAutopilot)heartbeat.Autopilot),
                _logger, nameof(HeartbeatReceived));
        }

        Subscribers.Raise(FrameReceived, new MavlinkFrameInfo(packet.SystemId, packet.ComponentId, packet.MessageId), _logger, nameof(FrameReceived));

        if (packet.Message is AdsbVehicleMessage adsb)
        {
            Subscribers.Raise(TrafficReceived, Traffic(adsb, packet.SystemId), _logger, nameof(TrafficReceived));
        }

        if (_vehicles.TryGetValue(packet.SystemId, out var vehicle))
        {
            vehicle.Handle(packet);
        }
    }

    private bool IsFollowedAutopilot(MavlinkPacket packet, HeartbeatMessage heartbeat) =>
        heartbeat.Autopilot != (byte)MavAutopilot.Invalid
        && heartbeat.Type != (byte)MavType.Gcs
        && (_options.TargetSystemId is null || _options.TargetSystemId == packet.SystemId);

    private void Discover(MavlinkPacket packet, HeartbeatMessage heartbeat)
    {
        var autopilot = (MavAutopilot)heartbeat.Autopilot;
        var profile = AutopilotProfiles.FromName(_options.AutopilotProfile) ?? AutopilotProfiles.FromAutopilot(autopilot);
        var vehicle = new VehicleSession(SendAsync, packet.SystemId, packet.ComponentId, autopilot, (MavType)heartbeat.Type, profile, _logger,
            _options.SystemId, _options.ComponentId);
        _vehicles[packet.SystemId] = vehicle;
        _logger.LogInformation("Vehicle {System} ({Autopilot} {Type}, profile {Profile}) on {Address}",
            packet.SystemId, autopilot, (MavType)heartbeat.Type, profile.Name, Address);
        Subscribers.Raise(VehicleDiscovered, vehicle, _logger, nameof(VehicleDiscovered));

        // Not streamed by default on every link; asked once on connect.
        _ = vehicle.Commands.RequestMessageAsync(HomePositionMessage.WireId);
        _ = vehicle.Commands.RequestMessageAsync(AutopilotVersionMessage.WireId);
    }

    private static AdsbTraffic Traffic(AdsbVehicleMessage m, byte source)
    {
        var flags = (AdsbFlags)m.Flags;
        bool Valid(AdsbFlags flag) => (flags & flag) != 0;
        var callsign = m.Callsign.Trim();
        return new AdsbTraffic(
            m.IcaoAddress,
            Valid(AdsbFlags.ValidCallsign) && callsign.Length > 0 ? callsign : null,
            Valid(AdsbFlags.ValidCoords) ? m.Lat / 1e7 : null,
            Valid(AdsbFlags.ValidCoords) ? m.Lon / 1e7 : null,
            Valid(AdsbFlags.ValidAltitude) ? m.Altitude / 1000.0 : null,
            (AdsbAltitudeType)m.AltitudeType,
            Valid(AdsbFlags.ValidHeading) ? m.Heading / 100.0 : null,
            Valid(AdsbFlags.ValidVelocity) ? m.HorVelocity / 100.0 : null,
            Valid(AdsbFlags.VerticalVelocityValid) ? m.VerVelocity / 100.0 : null,
            Valid(AdsbFlags.ValidSquawk) ? m.Squawk : null,
            (AdsbEmitterType)m.EmitterType,
            m.Tslc,
            Valid(AdsbFlags.Simulated),
            source);
    }

    private void Record(byte[] frame)
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        try
        {
            recorder.Write(DateTime.UtcNow, frame);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _recorder, null, recorder), recorder))
            {
                _logger.LogError(exception, "Telemetry log on {Address} stopped", Address);
                _dispatch.Writer.TryWrite(new RecordingFailure(exception));
            }
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        var heartbeat = new HeartbeatMessage
        {
            Type = (byte)MavType.Gcs,
            Autopilot = (byte)MavAutopilot.Invalid,
            BaseMode = 0,
            CustomMode = 0,
            SystemStatus = (byte)MavState.Active,
            MavlinkVersion = 3
        };

        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            do
            {
                await SendAsync(heartbeat);
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task WatchdogLoopAsync()
    {
        using var timer = new PeriodicTimer(WatchdogInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                _dispatch.Writer.TryWrite(new WatchdogTick(DateTime.UtcNow));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>Stops the loops and closes the transport. The recorder is detached, not disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        _recorder = null;
        await _stop.CancelAsync();
        _transport.Received -= OnReceived;
        _dispatch.Writer.TryComplete();
        foreach (var vehicle in _vehicles.Values)
        {
            vehicle.Events.Dispose();
        }

        await _transport.DisposeAsync();
        try
        {
            await Task.WhenAll(_loops);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record WatchdogTick(DateTime NowUtc);

    private sealed record TransportFault(Exception Exception);

    private sealed record SendFailure(Exception Exception);

    private sealed record RecordingFailure(Exception Exception);
}
