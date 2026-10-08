using System.IO.Ports;

namespace Ketras.Mavlink.Transport;

/// <summary>Serial link, e.g. a telemetry radio or the autopilot's USB port.</summary>
internal sealed class SerialTransport : IMavlinkTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly CancellationTokenSource _stop = new();
    private SerialPort? _port;

    /// <summary>Creates a link on <paramref name="portName"/> (e.g. COM5 or /dev/ttyUSB0) at <paramref name="baudRate"/>; nothing opens until opened.</summary>
    public SerialTransport(string portName, int baudRate)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    /// <inheritdoc />
    public string Description => $"serial:{_portName}:{_baudRate}";

    /// <inheritdoc />
    public bool IsOpen => _port?.IsOpen == true && !_stop.IsCancellationRequested;

    /// <inheritdoc />
    public event Action<ReadOnlyMemory<byte>>? Received;

    /// <inheritdoc />
    public event Action<Exception>? Faulted;

    /// <inheritdoc />
    public Task OpenAsync(CancellationToken cancellation = default)
    {
        _port = new SerialPort(_portName, _baudRate) { ReadTimeout = SerialPort.InfiniteTimeout };
        _port.Open();
        _ = Task.Run(ReceiveLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation = default)
    {
        if (_port is not { IsOpen: true } port)
        {
            return;
        }

        await port.BaseStream.WriteAsync(data, cancellation);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _port!.BaseStream.ReadAsync(buffer, _stop.Token);
                if (read > 0)
                {
                    Received?.Invoke(buffer.AsMemory(0, read));
                }
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
        _port?.Dispose();
        return ValueTask.CompletedTask;
    }
}
