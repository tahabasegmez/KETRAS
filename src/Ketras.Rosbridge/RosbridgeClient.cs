using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ketras.Rosbridge;

/// <summary>
/// Minimal rosbridge v2 JSON client over one WebSocket connection: call_service with a response, advertise / publish,
/// subscribe / unsubscribe. Topic messages are delivered through <see cref="MessageReceived"/> on the client's own
/// dispatch thread, never on the receive loop. One instance is one connection; <see cref="RosDispatcher"/> creates a new
/// one to reconnect.
/// </summary>
public sealed class RosbridgeClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _calls = new();
    private readonly Channel<(string Topic, JsonNode Message)> _incoming = Channel.CreateUnbounded<(string, JsonNode)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private int _nextId;
    private int _closed;

    /// <summary>Creates a client; nothing connects until <see cref="ConnectAsync"/>.</summary>
    public RosbridgeClient(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <summary>A topic message arrived: topic name and message. Raised on the client's dispatch thread.</summary>
    public event Action<string, JsonNode>? MessageReceived;

    /// <summary>Raised once when the connection ends; pending calls have failed by then.</summary>
    public event Action? Closed;

    /// <summary>True while the WebSocket is open.</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>Opens the WebSocket and starts receiving.</summary>
    public async Task ConnectAsync(Uri uri, CancellationToken cancellation)
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        await _socket.ConnectAsync(uri, cancellation);
        _ = Task.Run(ReceiveLoopAsync, CancellationToken.None);
        _ = Task.Run(DispatchLoopAsync, CancellationToken.None);
    }

    /// <summary>Calls a ROS service; returns the response <c>values</c>. Throws <see cref="InvalidOperationException"/>
    /// when rosbridge reports <c>result: false</c>, <see cref="TimeoutException"/> on timeout and
    /// <see cref="IOException"/> when the connection closes.</summary>
    public async Task<JsonNode?> CallAsync(string service, JsonObject args, TimeSpan timeout)
    {
        var id = $"call_{Interlocked.Increment(ref _nextId)}";
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = completion;
        try
        {
            await SendAsync(new JsonObject { ["op"] = "call_service", ["id"] = id, ["service"] = service, ["args"] = args });
            return await completion.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{service}: no response within {timeout.TotalSeconds:F0} s");
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    /// <summary>Creates the rosbridge-side publisher for <paramref name="topic"/> ahead of the first publish.</summary>
    public Task AdvertiseAsync(string topic, string type) =>
        SendAsync(new JsonObject { ["op"] = "advertise", ["topic"] = topic, ["type"] = type });

    /// <summary>Publishes one message. rosbridge sends no acknowledgement for it.</summary>
    public Task PublishAsync(string topic, JsonObject message) =>
        SendAsync(new JsonObject { ["op"] = "publish", ["topic"] = topic, ["msg"] = message });

    /// <summary>Asks rosbridge for a topic's messages. <paramref name="throttleMs"/> asks for at most one message per that
    /// many milliseconds (0 = every message).</summary>
    public Task SubscribeAsync(string topic, string type, int throttleMs = 0)
    {
        var message = new JsonObject { ["op"] = "subscribe", ["id"] = $"sub_{topic}", ["topic"] = topic, ["type"] = type };
        if (throttleMs > 0)
        {
            message["throttle_rate"] = throttleMs;
            message["queue_length"] = 1;
        }

        return SendAsync(message);
    }

    /// <summary>Ends the subscription made with <see cref="SubscribeAsync"/>.</summary>
    public Task UnsubscribeAsync(string topic) =>
        SendAsync(new JsonObject { ["op"] = "unsubscribe", ["id"] = $"sub_{topic}", ["topic"] = topic });

    private async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Route(text);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "rosbridge connection ended");
            }
        }

        Close();
    }

    /// <summary>Receive loop: answers calls and queues topic messages. A malformed message is logged and skipped.</summary>
    private void Route(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject node)
            {
                return;
            }

            switch (Text(node["op"]))
            {
                case "service_response" when Text(node["id"]) is { } id && _calls.TryGetValue(id, out var call):
                    if (node["result"] is JsonValue result && result.TryGetValue<bool>(out var ok) && !ok)
                    {
                        call.TrySetException(new InvalidOperationException($"{Text(node["service"])}: {node["values"]?.ToJsonString()}"));
                    }
                    else
                    {
                        call.TrySetResult(node["values"]);
                    }

                    break;
                case "publish" when Text(node["topic"]) is { } topic && node["msg"] is { } msg:
                    msg.Parent?.AsObject().Remove("msg");
                    _incoming.Writer.TryWrite((topic, msg));
                    break;
                case "status":
                    _logger.LogInformation("rosbridge status: {Message}", node["msg"]?.ToJsonString());
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(exception, "rosbridge sent a message that could not be read");
        }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var (topic, message) in _incoming.Reader.ReadAllAsync(_stop.Token))
            {
                foreach (var subscriber in MessageReceived?.GetInvocationList().Cast<Action<string, JsonNode>>() ?? [])
                {
                    try
                    {
                        subscriber(topic, message);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "A handler of {Topic} threw; the connection keeps running", topic);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        foreach (var call in _calls.Values)
        {
            call.TrySetException(new IOException("rosbridge connection closed"));
        }

        _incoming.Writer.TryComplete();
        Closed?.Invoke();
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Closes the WebSocket.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }

        _socket.Dispose();
        Close();
    }
}
