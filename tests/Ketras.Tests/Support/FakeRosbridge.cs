using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ketras.Tests.Support;

/// <summary>
/// A rosbridge stand-in on a local port: answers rosapi type lookups for a few standard types, records every
/// operation it receives, answers services from <see cref="Services"/>, and can push topic messages or drop clients.
/// </summary>
internal sealed class FakeRosbridge : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _clients = new();
    private readonly TaskCompletionSource _accepting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accept;

    private static readonly Dictionary<string, JsonArray> Layouts = new()
    {
        ["std_msgs/msg/String"] = [Typedef("std_msgs/String", ["data"], ["string"])],
        ["std_msgs/msg/Bool"] = [Typedef("std_msgs/Bool", ["data"], ["bool"])],
        ["std_srvs/srv/SetBool"] = [Typedef("std_srvs/SetBool_Request", ["data"], ["bool"])],
        ["geometry_msgs/msg/Pose"] =
        [
            Typedef("geometry_msgs/Pose", ["position", "orientation"], ["geometry_msgs/Point", "geometry_msgs/Quaternion"]),
            Typedef("geometry_msgs/Point", ["x", "y", "z"], ["float64", "float64", "float64"]),
            Typedef("geometry_msgs/Quaternion", ["x", "y", "z", "w"], ["float64", "float64", "float64", "float64"])
        ]
    };

    /// <param name="accepting">False holds every client in its handshake until <see cref="Accept"/>.</param>
    public FakeRosbridge(bool accepting = true)
    {
        if (accepting)
        {
            Accept();
        }

        _listener.Start();
        Uri = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _accept = Task.Run(AcceptLoopAsync);
    }

    public Uri Uri { get; }

    /// <summary>Every operation received, oldest first.</summary>
    public ConcurrentQueue<JsonObject> Received { get; } = new();

    /// <summary>Service name → response values; a service not listed gets no response.</summary>
    public ConcurrentDictionary<string, JsonObject> Services { get; } = new();

    public int Connections { get; private set; }

    /// <summary>Starts answering clients.</summary>
    public void Accept() => _accepting.TrySetResult();

    public IEnumerable<JsonObject> Ops(string op) => Received.Where(m => (string?)m["op"] == op);

    public async Task PublishAsync(string topic, JsonObject message) =>
        await SendAllAsync(new JsonObject { ["op"] = "publish", ["topic"] = topic, ["msg"] = message }.ToJsonString());

    public Task SendRawAsync(string text) => SendAllAsync(text);

    public async Task DropClientsAsync()
    {
        foreach (var socket in _clients.Keys)
        {
            socket.Abort();
            _clients.TryRemove(socket, out _);
        }

        await Task.Delay(50);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            await _accepting.Task.WaitAsync(_stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(tcp));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        using var connection = tcp;
        var stream = tcp.GetStream();
        var request = await ReadHeadersAsync(stream);
        var key = request.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));

        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        _clients[socket] = new SemaphoreSlim(1, 1);
        Connections++;
        var buffer = new byte[1 << 16];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)) is JsonObject op)
                {
                    Received.Enqueue(op);
                    await AnswerAsync(socket, op);
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            _clients.TryRemove(socket, out _);
        }
    }

    private async Task AnswerAsync(WebSocket socket, JsonObject op)
    {
        if ((string?)op["op"] != "call_service")
        {
            return;
        }

        var service = (string)op["service"]!;
        JsonNode? values = service switch
        {
            "/rosapi/message_details" or "/rosapi/service_request_details" =>
                new JsonObject { ["typedefs"] = Layouts.TryGetValue((string)op["args"]!["type"]!, out var layout) ? layout.DeepClone() : new JsonArray() },
            _ => Services.TryGetValue(service, out var response) ? response.DeepClone() : null
        };

        if (values is null)
        {
            return;
        }

        await SendAsync(socket, new JsonObject { ["op"] = "service_response", ["id"] = op["id"]!.DeepClone(), ["service"] = service, ["values"] = values, ["result"] = true }.ToJsonString());
    }

    private async Task SendAllAsync(string text)
    {
        foreach (var socket in _clients.Keys)
        {
            await SendAsync(socket, text);
        }
    }

    private async Task SendAsync(WebSocket socket, string text)
    {
        if (!_clients.TryGetValue(socket, out var gate))
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                break;
            }

            bytes.Add(one[0]);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static JsonObject Typedef(string type, string[] names, string[] types) => new()
    {
        ["type"] = type,
        ["fieldnames"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
        ["fieldtypes"] = new JsonArray(types.Select(t => (JsonNode)t).ToArray()),
        ["fieldarraylen"] = new JsonArray(names.Select(_ => (JsonNode)(-1)).ToArray())
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await DropClientsAsync();
        await _accept;
    }
}
