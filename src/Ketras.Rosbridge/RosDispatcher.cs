using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.Rosbridge;

/// <summary>
/// Keeps a rosbridge connection alive and sends requests over it.
/// <list type="bullet">
/// <item>Reconnects on its own; advertisements and subscriptions are restored on every connection.</item>
/// <item>Topics registered with <see cref="Advertise"/> get their publisher as soon as the connection opens, so the
/// first real publish does not wait for a new publisher to be matched.</item>
/// <item>Text parameters are converted to the field types reported by rosapi (<c>/rosapi/message_details</c>,
/// <c>/rosapi/service_request_details</c>); the lookup is cached per type.</item>
/// <item>Several handlers may listen to one topic; each runs on the connection's dispatch thread, isolated from the
/// others.</item>
/// </list>
/// Knows no system, topic or command names; the caller decides what to send.
/// </summary>
public sealed class RosDispatcher : IAsyncDisposable
{
    private const string MessageDetailsService = "/rosapi/message_details";
    private const string ServiceRequestDetailsService = "/rosapi/service_request_details";

    private readonly Uri _uri;
    private readonly RosDispatcherOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _advertisements = new();
    private readonly Dictionary<string, Subscription> _subscriptions = [];
    private readonly ConcurrentDictionary<string, IReadOnlyList<RosTypeDef>> _layouts = new();
    private readonly CancellationTokenSource _stop = new();
    private ConcurrentDictionary<string, bool> _advertisedOnThisConnection = new();
    private volatile RosbridgeClient? _client;
    private Task? _loop;

    /// <summary>Creates a dispatcher for a rosbridge WebSocket, e.g. <c>ws://localhost:9090</c>.</summary>
    public RosDispatcher(Uri uri, RosDispatcherOptions? options = null, ILogger? logger = null)
    {
        _uri = uri;
        _options = options ?? new RosDispatcherOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>rosbridge address.</summary>
    public Uri Address => _uri;

    /// <summary>True while connected to rosbridge.</summary>
    public bool IsConnected => _client?.IsOpen == true;

    /// <summary>Raised on a background thread when <see cref="IsConnected"/> changes.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Starts connecting in the background; returns at once.</summary>
    public void Start() => _loop ??= Task.Run(ConnectLoopAsync);

    /// <summary>Registers a topic this station publishes on, so its publisher exists before the first publish.</summary>
    public void Advertise(string topic, string type)
    {
        _advertisements[topic] = type;
        if (_client is { IsOpen: true } client)
        {
            _ = EnsureAdvertisedAsync(client, topic, type);
        }
    }

    /// <summary>
    /// Listens to a topic, across reconnections, until the returned handle is disposed. Several handlers may share a
    /// topic; the first one's <paramref name="throttleMs"/> applies.
    /// </summary>
    public async Task<IAsyncDisposable> SubscribeAsync(string topic, string type, Action<JsonNode> handler, int throttleMs = 0)
    {
        bool first;
        lock (_subscriptions)
        {
            first = !_subscriptions.TryGetValue(topic, out var subscription);
            subscription ??= _subscriptions[topic] = new Subscription(type, throttleMs);
            subscription.Handlers.Add(handler);
        }

        if (first && _client is { IsOpen: true } client)
        {
            await client.SubscribeAsync(topic, type, throttleMs);
        }

        return new Handle(this, topic, handler);
    }

    /// <summary>
    /// Publishes or calls with text parameters converted to the message's field types. A publish is
    /// <see cref="RosOutcome.Accepted"/> once sent: topics carry no acknowledgement. A service call is
    /// <see cref="RosOutcome.Rejected"/> when its response has a <c>success</c> field that is false, or, without one,
    /// when its only bool field is false.
    /// </summary>
    public async Task<RosResult> SendAsync(RosRequest request)
    {
        if (_client is not { IsOpen: true } client)
        {
            return NotConnected;
        }

        JsonObject payload;
        try
        {
            var layout = await GetLayoutAsync(client, request.Interaction, request.Type);
            payload = RosMessageBuilder.Build(layout, request.Parameters);
        }
        catch (Exception exception) when (exception is FormatException or TimeoutException or InvalidOperationException or IOException)
        {
            return new RosResult(RosOutcome.Failed, $"{request.Name}: {exception.Message}");
        }

        return await SendCoreAsync(client, request.Interaction, request.Name, request.Type, payload);
    }

    /// <summary>Publishes or calls with a message the caller already built as JSON (no type lookup).</summary>
    public async Task<RosResult> SendJsonAsync(RosInteraction interaction, string name, string type, JsonObject message) =>
        _client is { IsOpen: true } client ? await SendCoreAsync(client, interaction, name, type, message) : NotConnected;

    private static RosResult NotConnected => new(RosOutcome.Failed, "not connected to rosbridge");

    private async Task<RosResult> SendCoreAsync(RosbridgeClient client, RosInteraction interaction, string name, string type, JsonObject payload)
    {
        try
        {
            if (interaction == RosInteraction.Publish)
            {
                await EnsureAdvertisedAsync(client, name, type);
                await client.PublishAsync(name, payload);
                return new RosResult(RosOutcome.Accepted, "published (topics carry no acknowledgement)");
            }

            var values = await client.CallAsync(name, payload, _options.ServiceTimeout);
            return Evaluate(values);
        }
        catch (TimeoutException exception)
        {
            return new RosResult(RosOutcome.NoResponse, exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            return new RosResult(RosOutcome.Failed, exception.Message);
        }
    }

    private static RosResult Evaluate(JsonNode? values)
    {
        if (values is not JsonObject response)
        {
            return new RosResult(RosOutcome.Accepted, string.Empty, values);
        }

        var flags = response.Where(p => p.Value is JsonValue v && v.TryGetValue<bool>(out _)).ToList();
        bool? success = response["success"] is JsonValue s && s.TryGetValue<bool>(out var ok) ? ok
            : flags.Count == 1 ? flags[0].Value!.GetValue<bool>()
            : null;
        var message = response.Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
            .Select(p => p.Value!.GetValue<string>())
            .FirstOrDefault() ?? string.Empty;

        return success == false
            ? new RosResult(RosOutcome.Rejected, message.Length > 0 ? message : "service reported failure", values)
            : new RosResult(RosOutcome.Accepted, message, values);
    }

    private async Task<IReadOnlyList<RosTypeDef>> GetLayoutAsync(RosbridgeClient client, RosInteraction interaction, string type)
    {
        var key = $"{interaction}:{type}";
        if (_layouts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var service = interaction == RosInteraction.Publish ? MessageDetailsService : ServiceRequestDetailsService;
        var response = await client.CallAsync(service, new JsonObject { ["type"] = type }, _options.TypeLookupTimeout);
        var layout = RosTypeDef.ParseList(response?["typedefs"]);
        if (layout.Count == 0)
        {
            throw new FormatException($"unknown type {type} (rosapi returned no layout)");
        }

        _layouts[key] = layout;
        return layout;
    }

    private async Task EnsureAdvertisedAsync(RosbridgeClient client, string topic, string type)
    {
        if (_advertisedOnThisConnection.TryAdd(topic, true))
        {
            await client.AdvertiseAsync(topic, type);
        }
    }

    private void Deliver(string topic, JsonNode message)
    {
        Action<JsonNode>[] handlers;
        lock (_subscriptions)
        {
            handlers = _subscriptions.TryGetValue(topic, out var subscription) ? [.. subscription.Handlers] : [];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(message);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "A handler of {Topic} threw; the connection keeps running", topic);
            }
        }
    }

    private async Task RemoveAsync(string topic, Action<JsonNode> handler)
    {
        bool last;
        lock (_subscriptions)
        {
            if (!_subscriptions.TryGetValue(topic, out var subscription) || !subscription.Handlers.Remove(handler))
            {
                return;
            }

            last = subscription.Handlers.Count == 0;
            if (last)
            {
                _subscriptions.Remove(topic);
            }
        }

        if (last && _client is { IsOpen: true } client)
        {
            try
            {
                await client.UnsubscribeAsync(topic);
            }
            catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection is gone; rosbridge drops its subscriptions with it.
            }
        }
    }

    private async Task ConnectLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = new RosbridgeClient(_logger);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Closed += () => closed.TrySetResult();
            client.MessageReceived += Deliver;
            var connected = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(_options.ConnectTimeout);
                await client.ConnectAsync(_uri, timeout.Token);

                _advertisedOnThisConnection = new ConcurrentDictionary<string, bool>();
                _client = client;
                connected = true;
                foreach (var (topic, type) in _advertisements)
                {
                    await EnsureAdvertisedAsync(client, topic, type);
                }

                List<(string Topic, Subscription Subscription)> subscriptions;
                lock (_subscriptions)
                {
                    subscriptions = _subscriptions.Select(p => (p.Key, p.Value)).ToList();
                }

                foreach (var (topic, subscription) in subscriptions)
                {
                    await client.SubscribeAsync(topic, subscription.Type, subscription.ThrottleMs);
                }

                _logger.LogInformation("rosbridge connected: {Uri}", _uri);
                RaiseConnectionChanged(true);
                await closed.Task.WaitAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested && !connected)
                {
                    _logger.LogDebug("rosbridge {Uri} not reachable: {Reason}", _uri, exception.Message);
                }
            }

            _client = null;
            await client.DisposeAsync();
            if (connected)
            {
                RaiseConnectionChanged(false);
            }

            try
            {
                await Task.Delay(_options.ReconnectDelay, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RaiseConnectionChanged(bool connected)
    {
        foreach (var subscriber in ConnectionChanged?.GetInvocationList().Cast<Action<bool>>() ?? [])
        {
            try
            {
                subscriber(connected);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "A ConnectionChanged subscriber threw");
            }
        }
    }

    /// <summary>Stops reconnecting and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }
    }

    private sealed record Subscription(string Type, int ThrottleMs)
    {
        public List<Action<JsonNode>> Handlers { get; } = [];
    }

    private sealed class Handle(RosDispatcher owner, string topic, Action<JsonNode> handler) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await owner.RemoveAsync(topic, handler);
            }
        }
    }
}
