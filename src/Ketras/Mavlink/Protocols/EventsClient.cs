using Ketras.Mavlink.Messages;
using Microsoft.Extensions.Logging;

namespace Ketras.Mavlink.Protocols;

/// <summary>
/// One event from the vehicle (MAVLink EVENT), not yet interpreted: <see cref="Id"/> selects its definition in the
/// vehicle's events metadata, which also says how <see cref="Arguments"/> are laid out.
/// </summary>
/// <param name="Id">Event id: the metadata component in the top 8 bits, the event in the lower 24.</param>
/// <param name="Sequence">Sequence number.</param>
/// <param name="TimeBootMs">Vehicle time since boot.</param>
/// <param name="LogLevels">External log level in the low 4 bits, internal in the high 4 (libevents).</param>
/// <param name="Arguments">The packed little-endian arguments (40 bytes).</param>
public sealed record VehicleEvent(uint Id, ushort Sequence, uint TimeBootMs, byte LogLevels, byte[] Arguments)
{
    /// <summary>The level for the operator: 0 Emergency … 7 Debug, 8 Protocol (not for display), 9 Disabled.</summary>
    public int ExternalLogLevel => LogLevels & 0x0F;
}

/// <summary>
/// Receiver side of the MAVLink events protocol for the autopilot component, as libevents' ReceiveProtocol
/// (libs/cpp/protocol/receive.h): events are delivered in sequence order exactly once; a gap is filled with
/// REQUEST_EVENT (re-sent every 100 ms, for up to 3 s), CURRENT_EVENT_SEQUENCE announces events not received yet,
/// RESPONSE_EVENT_ERROR skips events the vehicle no longer has, and a reset flag or a jump back in the vehicle's boot
/// time restarts the sequence. Events arrive on the connection's dispatch thread.
/// </summary>
public sealed class EventsClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(100);
    private const int MaxRetries = 30;

    private readonly Func<IMavlinkMessage, ValueTask> _send;
    private readonly byte _systemId;
    private readonly byte _componentId;
    private readonly byte _ownSystemId;
    private readonly byte _ownComponentId;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _hasSequence;
    private ushort _latestSequence;
    private bool _hasCurrentSequence;
    private ushort _latestCurrentSequence;
    private uint _lastTimestampMs;
    private int _retries;

    internal EventsClient(Func<IMavlinkMessage, ValueTask> send, byte systemId, byte componentId, byte ownSystemId, byte ownComponentId, ILogger logger)
    {
        _send = send;
        _systemId = systemId;
        _componentId = componentId;
        _ownSystemId = ownSystemId;
        _ownComponentId = ownComponentId;
        _logger = logger;
        _timer = new Timer(_ => OnTimeout(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>An event in sequence; raised on the dispatch thread.</summary>
    public event Action<VehicleEvent>? Received;

    /// <summary>Events the vehicle no longer had when they were requested (the count); raised on the dispatch thread.</summary>
    public event Action<int>? Lost;

    /// <summary>True once the vehicle has sent an event or its current sequence: it speaks the events protocol.</summary>
    public bool IsSupported
    {
        get
        {
            lock (_lock)
            {
                return _hasSequence;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    internal void Handle(IMavlinkMessage message)
    {
        switch (message)
        {
            case EventMessage @event:
                HandleEvent(@event);
                break;
            case CurrentEventSequenceMessage current:
                HandleCurrentSequence(current);
                break;
            case ResponseEventErrorMessage error:
                HandleError(error);
                break;
        }
    }

    private void HandleEvent(EventMessage message)
    {
        lock (_lock)
        {
            CheckTimestampReset(message.EventTimeBootMs);
            if (!_hasSequence)
            {
                _hasSequence = true;
                _latestSequence = (ushort)(message.Sequence - 1);
            }

            switch (Compare((ushort)(_latestSequence + 1), message.Sequence))
            {
                case < 0:
                    return;
                case > 0:
                    // A gap: ask for the next expected one; this event comes again in order.
                    Request((ushort)(_latestSequence + 1));
                    return;
            }

            _latestSequence = message.Sequence;
            _lastTimestampMs = message.EventTimeBootMs;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_hasCurrentSequence && Compare(_latestSequence, (ushort)_latestCurrentSequence) > 0)
            {
                Request((ushort)(_latestSequence + 1));
            }
        }

        if ((message.DestinationSystem != 0 && message.DestinationSystem != _ownSystemId)
            || (message.DestinationComponent != 0 && message.DestinationComponent != _ownComponentId))
        {
            return;
        }

        var @event = new VehicleEvent(message.Id, message.Sequence, message.EventTimeBootMs, message.LogLevels, message.Arguments);
        Subscribers.Raise(Received, @event, _logger, nameof(Received));
    }

    private void HandleCurrentSequence(CurrentEventSequenceMessage message)
    {
        const byte ResetFlag = 1;
        lock (_lock)
        {
            if ((message.Flags & ResetFlag) != 0)
            {
                _hasSequence = false;
            }

            if (!_hasSequence)
            {
                _hasSequence = true;
                _latestSequence = message.Sequence;
            }

            if (Compare(_latestSequence, message.Sequence) > 0)
            {
                Request((ushort)(_latestSequence + 1));
            }

            _hasCurrentSequence = true;
            _latestCurrentSequence = message.Sequence;
        }
    }

    private void HandleError(ResponseEventErrorMessage message)
    {
        int lost;
        lock (_lock)
        {
            if (message.TargetSystem != _ownSystemId || message.TargetComponent != _ownComponentId
                || Compare((ushort)(_latestSequence + 1), message.Sequence) != 0)
            {
                return;
            }

            lost = (ushort)(message.SequenceOldestAvailable - _latestSequence - 1);
            _latestSequence = (ushort)(message.SequenceOldestAvailable - 1);
            Request((ushort)(_latestSequence + 1));
        }

        Subscribers.Raise(Lost, lost, _logger, nameof(Lost));
    }

    private void OnTimeout()
    {
        lock (_lock)
        {
            // Unlike receive.h, give up after a while: the next CURRENT_EVENT_SEQUENCE asks again.
            if (++_retries <= MaxRetries)
            {
                Request((ushort)(_latestSequence + 1), retry: true);
            }
        }
    }

    /// <summary>A vehicle reboot: its boot time went back by more than 10 s (receive.h checkTimestampReset).</summary>
    private void CheckTimestampReset(uint timestampMs)
    {
        if (_lastTimestampMs == 0)
        {
            _lastTimestampMs = timestampMs;
        }

        if ((long)timestampMs + 10000 < _lastTimestampMs && _lastTimestampMs < uint.MaxValue - 60000)
        {
            _hasSequence = false;
            _hasCurrentSequence = false;
        }
    }

    /// <summary>0 when equal, negative when <paramref name="incoming"/> is older (a duplicate), positive when newer (a gap); wraps.</summary>
    private static int Compare(ushort expected, ushort incoming)
    {
        if (expected == incoming)
        {
            return 0;
        }

        return (ushort)(incoming - expected) > ushort.MaxValue / 2 ? -1 : 1;
    }

    private void Request(ushort sequence, bool retry = false)
    {
        if (!retry)
        {
            _retries = 0;
        }

        _timer.Change(RequestTimeout, Timeout.InfiniteTimeSpan);
        _ = SendAsync(new RequestEventMessage { TargetSystem = _systemId, TargetComponent = _componentId, FirstSequence = sequence, LastSequence = sequence });
    }

    private async Task SendAsync(IMavlinkMessage message)
    {
        try
        {
            await _send(message);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "REQUEST_EVENT not sent");
        }
    }
}
