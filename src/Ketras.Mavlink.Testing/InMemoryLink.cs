using System.Threading.Channels;
using Ketras.Mavlink.Transport;

namespace Ketras.Mavlink.Testing;

/// <summary>
/// Two connected in-memory transports: bytes sent on one arrive on the other, on a background thread, like a real
/// link. Each end can be told to fail its sends or to fault, to exercise link-loss handling.
/// </summary>
public sealed class InMemoryLink
{
    private InMemoryLink()
    {
        Station = new End("memory://station");
        Vehicle = new End("memory://vehicle");
        Station.Peer = Vehicle;
        Vehicle.Peer = Station;
    }

    /// <summary>The ground station's end.</summary>
    public End Station { get; }

    /// <summary>The vehicle's end.</summary>
    public End Vehicle { get; }

    /// <summary>Creates a connected pair.</summary>
    public static InMemoryLink Create() => new();

    /// <summary>One end of the link.</summary>
    public sealed class End : IMavlinkTransport
    {
        private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _stop = new();
        private bool _faulted;

        internal End(string description) => Description = description;

        internal End? Peer { get; set; }

        /// <inheritdoc />
        public string Description { get; }

        /// <inheritdoc />
        public bool IsOpen { get; private set; }

        /// <summary>While set, <see cref="SendAsync"/> throws an <see cref="IOException"/>, as a transport without a network does.</summary>
        public bool FailSends { get; set; }

        /// <summary>While set, bytes sent to this end are dropped, as on a link that lost its signal.</summary>
        public bool DropIncoming { get; set; }

        /// <inheritdoc />
        public event Action<ReadOnlyMemory<byte>>? Received;

        /// <inheritdoc />
        public event Action<Exception>? Faulted;

        /// <inheritdoc />
        public Task OpenAsync(CancellationToken cancellation = default)
        {
            IsOpen = true;
            _ = Task.Run(ReceiveLoopAsync, CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation = default)
        {
            if (FailSends)
            {
                throw new IOException("in-memory link: send refused");
            }

            if (IsOpen && Peer is { IsOpen: true, DropIncoming: false } peer)
            {
                peer._inbox.Writer.TryWrite(data.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>Stops receiving with <paramref name="error"/>, as a transport whose cable was pulled.</summary>
        public void Fault(Exception error)
        {
            if (_faulted)
            {
                return;
            }

            _faulted = true;
            IsOpen = false;
            _stop.Cancel();
            Faulted?.Invoke(error);
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                await foreach (var chunk in _inbox.Reader.ReadAllAsync(_stop.Token))
                {
                    Received?.Invoke(chunk);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            _stop.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
