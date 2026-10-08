using System.Net.Sockets;

namespace Ketras.Mavlink.Transport;

/// <summary>TCP client link, e.g. to a MAVLink router or a simulator's TCP port.</summary>
internal sealed class TcpTransport : IMavlinkTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <summary>Creates a link to <paramref name="host"/>:<paramref name="port"/>; nothing connects until opened.</summary>
    public TcpTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <inheritdoc />
    public string Description => $"tcp://{_host}:{_port}";

    /// <inheritdoc />
    public bool IsOpen => _client?.Connected == true && !_stop.IsCancellationRequested;

    /// <inheritdoc />
    public event Action<ReadOnlyMemory<byte>>? Received;

    /// <inheritdoc />
    public event Action<Exception>? Faulted;

    /// <inheritdoc />
    public async Task OpenAsync(CancellationToken cancellation = default)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(_host, _port, cancellation);
        _stream = _client.GetStream();
        _ = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation = default)
    {
        if (_stream is null)
        {
            return;
        }

        await _stream.WriteAsync(data, cancellation);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _stream!.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    throw new IOException("connection closed by peer");
                }

                Received?.Invoke(buffer.AsMemory(0, read));
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
        _stream?.Dispose();
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
