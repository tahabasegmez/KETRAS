using System.Collections.Concurrent;
using Ketras.Autopilots;
using Ketras.Mavlink.Messages;
using Microsoft.Extensions.Logging;

namespace Ketras.Mavlink.Protocols;

/// <summary>One parameter as the vehicle reported it.</summary>
public sealed record ParameterEntry(string Name, ParameterValue Value, int Index, int Count);

/// <summary>Result of a full list read.</summary>
public sealed record ParameterListResult(bool IsComplete, int Expected, IReadOnlyDictionary<string, ParameterEntry> Values);

/// <summary>Result of a write: <see cref="IsApplied"/> only when the vehicle echoed the requested value.</summary>
public sealed record ParameterWriteResult(bool IsApplied, ParameterEntry? Echoed, string Reason = "");

/// <summary>
/// MAVLink parameter protocol. Full list with re-requests of missing indices, single reads, and writes
/// confirmed by the echoed PARAM_VALUE. Value packing comes from the autopilot profile (PX4: bytewise).
/// Timings are in docs/reference. Events are raised on the connection's dispatch thread.
/// </summary>
public sealed class ParameterClient
{
    private const string HashCheck = "_HASH_CHECK";
    private const ushort NoIndex = ushort.MaxValue;
    private static readonly TimeSpan FirstValueTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListGapTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);
    private const int MissingRounds = 5;
    private const int SingleTries = 3;

    private readonly Func<IMavlinkMessage, ValueTask> _send;
    private readonly byte _targetSystem;
    private readonly byte _targetComponent;
    private readonly IAutopilotProfile _profile;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ParameterEntry> _cache = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ParameterEntry>> _waiting = new();
    private readonly SemaphoreSlim _listGate = new(1, 1);
    private readonly SemaphoreSlim _listSignal = new(0);
    private readonly object _listLock = new();
    private HashSet<int>? _listReceived;
    private int _listExpected = -1;

    internal ParameterClient(Func<IMavlinkMessage, ValueTask> send, byte targetSystem, byte targetComponent, IAutopilotProfile profile, ILogger logger)
    {
        _send = send;
        _logger = logger;
        _targetSystem = targetSystem;
        _targetComponent = targetComponent;
        _profile = profile;
    }

    /// <summary>Every value reported so far, by name.</summary>
    public IReadOnlyDictionary<string, ParameterEntry> Cache => _cache;

    /// <summary>Every PARAM_VALUE, solicited or not (a value changed by another client arrives here too).</summary>
    public event Action<ParameterEntry>? ValueReceived;

    /// <summary>Full-list progress: received, expected.</summary>
    public event Action<int, int>? ListProgress;

    /// <summary>Reads every parameter. Incomplete only if values are still missing after the re-request rounds.</summary>
    public async Task<ParameterListResult> RefreshAllAsync(CancellationToken cancellation = default)
    {
        await _listGate.WaitAsync(cancellation);
        try
        {
            lock (_listLock)
            {
                _listReceived = [];
                _listExpected = -1;
            }

            while (_listSignal.CurrentCount > 0)
            {
                await _listSignal.WaitAsync(cancellation);
            }

            await _send(new ParamRequestListMessage { TargetSystem = _targetSystem, TargetComponent = _targetComponent });

            if (!await _listSignal.WaitAsync(FirstValueTimeout, cancellation))
            {
                return Finish(false);
            }

            for (var round = 0; ; round++)
            {
                while (!IsListComplete() && await _listSignal.WaitAsync(ListGapTimeout, cancellation))
                {
                }

                if (IsListComplete())
                {
                    return Finish(true);
                }

                if (round >= MissingRounds)
                {
                    return Finish(false);
                }

                foreach (var index in MissingIndices())
                {
                    await _send(new ParamRequestReadMessage
                    {
                        TargetSystem = _targetSystem,
                        TargetComponent = _targetComponent,
                        ParamIndex = (short)index
                    });
                }
            }
        }
        finally
        {
            lock (_listLock)
            {
                _listReceived = null;
            }

            _listGate.Release();
        }
    }

    /// <summary>Reads one parameter by name; null if the vehicle does not answer.</summary>
    public async Task<ParameterEntry?> ReadAsync(string name, CancellationToken cancellation = default)
    {
        var request = new ParamRequestReadMessage
        {
            TargetSystem = _targetSystem,
            TargetComponent = _targetComponent,
            ParamId = name,
            ParamIndex = -1
        };

        return await RequestAsync(name, request, cancellation);
    }

    /// <summary>
    /// Writes one parameter and waits for the echo. The type must be the parameter's own type
    /// (PX4 accepts only INT32 and REAL32 and refuses a mismatch).
    /// </summary>
    public async Task<ParameterWriteResult> WriteAsync(string name, ParameterValue value, CancellationToken cancellation = default)
    {
        var request = new ParamSetMessage
        {
            TargetSystem = _targetSystem,
            TargetComponent = _targetComponent,
            ParamId = name,
            ParamValue = _profile.EncodeParameter(value),
            ParamType = (byte)value.Type
        };

        var echoed = await RequestAsync(name, request, cancellation);
        if (echoed is null)
        {
            return new ParameterWriteResult(false, null, "no echo from the vehicle");
        }

        return Matches(echoed.Value, value)
            ? new ParameterWriteResult(true, echoed)
            : new ParameterWriteResult(false, echoed, "the vehicle kept a different value");
    }

    internal void Handle(ParamValueMessage message)
    {
        if (message.ParamIndex == NoIndex && message.ParamId == HashCheck)
        {
            return;
        }

        var entry = new ParameterEntry(
            message.ParamId,
            _profile.DecodeParameter(message.ParamValue, (MavParamType)message.ParamType),
            message.ParamIndex,
            message.ParamCount);

        _cache[entry.Name] = entry;

        if (_waiting.TryRemove(entry.Name, out var waiter))
        {
            waiter.TrySetResult(entry);
        }

        var progress = (Received: -1, Expected: -1);
        lock (_listLock)
        {
            if (_listReceived is not null && message.ParamIndex != NoIndex)
            {
                _listExpected = message.ParamCount;
                _listReceived.Add(message.ParamIndex);
                progress = (_listReceived.Count, _listExpected);
            }
        }

        Subscribers.Raise(ValueReceived, entry, _logger, nameof(ValueReceived));

        if (progress.Received >= 0)
        {
            Subscribers.Raise(ListProgress, progress.Received, progress.Expected, _logger, nameof(ListProgress));
            _listSignal.Release();
        }
    }

    private async Task<ParameterEntry?> RequestAsync(string name, IMavlinkMessage request, CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < SingleTries; attempt++)
        {
            // Concurrent requests for one name share a waiter, so none of them loses the reply to another.
            var waiter = _waiting.GetOrAdd(name, _ => new TaskCompletionSource<ParameterEntry>(TaskCreationOptions.RunContinuationsAsynchronously));
            await _send(request);

            var finished = await Task.WhenAny(waiter.Task, Task.Delay(ReplyTimeout, cancellation));
            if (finished == waiter.Task)
            {
                return waiter.Task.Result;
            }

            cancellation.ThrowIfCancellationRequested();
            _waiting.TryRemove(new KeyValuePair<string, TaskCompletionSource<ParameterEntry>>(name, waiter));
        }

        return null;
    }

    private bool IsListComplete()
    {
        lock (_listLock)
        {
            return _listReceived is not null && _listExpected >= 0 && _listReceived.Count >= _listExpected;
        }
    }

    private List<int> MissingIndices()
    {
        lock (_listLock)
        {
            return Enumerable.Range(0, Math.Max(_listExpected, 0)).Where(i => !_listReceived!.Contains(i)).ToList();
        }
    }

    private ParameterListResult Finish(bool complete)
    {
        int expected;
        lock (_listLock)
        {
            expected = _listExpected;
        }

        return new ParameterListResult(complete, expected, new Dictionary<string, ParameterEntry>(_cache));
    }

    /// <summary>Integers compare exactly; floats at single precision, which is what the vehicle stores.</summary>
    private static bool Matches(ParameterValue echoed, ParameterValue requested) =>
        echoed.IsInteger
            ? Math.Abs(echoed.Value - Math.Round(requested.Value)) < 0.5
            : Math.Abs((float)echoed.Value - (float)requested.Value) <= Math.Max(1e-6f, Math.Abs((float)requested.Value) * 1e-6f);
}
