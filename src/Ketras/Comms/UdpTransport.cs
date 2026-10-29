using System.Net;
using System.Net.Sockets;

namespace Ketras.Comms;

/// <summary>
/// UDP link. Listening mode binds a local port and answers whoever sent the last datagram, the way a ground station
/// waits for an autopilot (PX4 SITL sends to 14550). Peer mode sends to a fixed address from an ephemeral port.
/// </summary>
internal sealed class UdpTransport : IMavlinkTransport
{
    private readonly int _localPort;
    private readonly string? _peerHost;
    private readonly int _peerPort;
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _client;
    private volatile IPEndPoint? _peer;

    private UdpTransport(int localPort, string? peerHost, int peerPort, string description)
    {
        _localPort = localPort;
        _peerHost = peerHost;
        _peerPort = peerPort;
        Description = description;
    }

    /// <summary>Listens on a local port; replies go to the last sender.</summary>
    public static UdpTransport Listen(int localPort) => new(localPort, null, 0, $"udp://:{localPort}");

    /// <summary>Sends to a fixed host and port, resolved when the link opens.</summary>
    public static UdpTransport Connect(string host, int port) => new(0, host, port, $"udp://{host}:{port}");

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public bool IsOpen => _client is not null && !_stop.IsCancellationRequested;

    /// <inheritdoc />
    public event Action<ReadOnlyMemory<byte>>? Received;

    /// <inheritdoc />
    public event Action<Exception>? Faulted;

    /// <inheritdoc />
    public async Task OpenAsync(CancellationToken cancellation = default)
    {
        if (_peerHost is not null)
        {
            var addresses = await Dns.GetHostAddressesAsync(_peerHost, cancellation);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
            _peer = new IPEndPoint(address, _peerPort);
        }

        var family = _peer?.AddressFamily ?? AddressFamily.InterNetwork;
        _client = new UdpClient(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, _localPort));
        _ = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation = default)
    {
        if (_client is null || _peer is not { } peer)
        {
            return;
        }

        await _client.SendAsync(data, peer, cancellation);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _client!.ReceiveAsync(_stop.Token);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows reports an ICMP "port unreachable" for an earlier send (peer not up yet) here.
                    continue;
                }

                if (_peerHost is null)
                {
                    _peer = result.RemoteEndPoint;
                }

                Received?.Invoke(result.Buffer);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_stop.IsCancellationRequested)
            {
                Faulted?.Invoke(exception);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
