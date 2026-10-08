using Ketras.Mavlink.Autopilots;
using Ketras.Mavlink.Protocols;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Vehicles;

/// <summary>
/// One vehicle's parameters: the full list is read once on connect, and concurrent refreshes
/// share one transfer. Values the vehicle reports on its own (another station writing, a calibration) update the cache.
/// Events are raised on the link's dispatch thread or the caller's.
/// </summary>
public sealed class VehicleParameters
{
    private readonly ParameterClient _client;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Task<ParameterListResult>? _refresh;

    internal VehicleParameters(ParameterClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
        client.ValueReceived += entry => ValueChanged?.Invoke(entry);
        client.ListProgress += (received, total) => Progress?.Invoke(received, total);
    }

    /// <summary>True once a full list completed.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>True while a full list transfer runs.</summary>
    public bool IsRefreshing
    {
        get
        {
            lock (_lock)
            {
                return _refresh is { IsCompleted: false };
            }
        }
    }

    /// <summary>Every value reported so far, by name.</summary>
    public IReadOnlyDictionary<string, ParameterEntry> Values => _client.Cache;

    /// <summary>A full list finished (complete or not).</summary>
    public event Action<ParameterListResult>? Loaded;

    /// <summary>A value arrived (any read, write echo or unsolicited report).</summary>
    public event Action<ParameterEntry>? ValueChanged;

    /// <summary>Full-list progress: received, expected.</summary>
    public event Action<int, int>? Progress;

    /// <summary>The value of <paramref name="name"/> if it was reported.</summary>
    public double? this[string name] => _client.Cache.TryGetValue(name, out var entry) ? entry.Value.Value : null;

    /// <summary>Reads the full list; callers during a transfer share it.</summary>
    public Task<ParameterListResult> RefreshAsync()
    {
        lock (_lock)
        {
            if (_refresh is { IsCompleted: false } running)
            {
                return running;
            }

            _refresh = RunRefreshAsync();
            return _refresh;
        }
    }

    /// <summary>Reads one parameter; null when the vehicle does not answer.</summary>
    public Task<ParameterEntry?> ReadAsync(string name, CancellationToken cancellation = default) => _client.ReadAsync(name, cancellation);

    /// <summary>Writes one parameter in its own type; applied only when the vehicle echoes the value.</summary>
    public Task<ParameterWriteResult> WriteAsync(string name, ParameterValue value, CancellationToken cancellation = default) =>
        _client.WriteAsync(name, value, cancellation);

    private async Task<ParameterListResult> RunRefreshAsync()
    {
        var result = await _client.RefreshAllAsync();
        IsLoaded |= result.IsComplete;
        _logger.LogInformation(result.IsComplete ? "Parameters: {Count} loaded" : "Parameters: {Count} of {Expected} loaded", result.Values.Count, result.Expected);
        Loaded?.Invoke(result);
        return result;
    }
}
