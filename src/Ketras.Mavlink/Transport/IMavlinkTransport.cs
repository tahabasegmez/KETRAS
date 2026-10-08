namespace Ketras.Mavlink.Transport;

/// <summary>
/// Strategy: a byte pipe to a vehicle or a MAVLink router; framing is done above it. Implement it for a link the
/// library does not ship (e.g. Bluetooth) and pass it to <see cref="MavlinkConnection.Create(IMavlinkTransport, MavlinkConnectionOptions?, Microsoft.Extensions.Logging.ILogger?)"/>.
/// </summary>
public interface IMavlinkTransport : IAsyncDisposable
{
    /// <summary>Human-readable address, e.g. "udp://:14550".</summary>
    string Description { get; }

    /// <summary>True once opened, until closed or failed.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Raised on the transport's receive thread for every chunk that arrives. The memory is only valid during the
    /// call; a subscriber must copy what it keeps and must not block.
    /// </summary>
    event Action<ReadOnlyMemory<byte>>? Received;

    /// <summary>Raised once, on the receive thread, when the transport stops receiving because of an error.</summary>
    event Action<Exception>? Faulted;

    /// <summary>Opens the link and starts receiving.</summary>
    Task OpenAsync(CancellationToken cancellation = default);

    /// <summary>Sends one frame. A transport that has no peer yet (listening UDP) drops it. Throws on a send failure.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation = default);
}
