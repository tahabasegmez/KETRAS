using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Testing;
using Ketras.Mavlink.Vehicles;

namespace Ketras.Mavlink.Tests;

/// <summary>A started <see cref="FakeVehicle"/> and a station connection to it, with a short link timeout.</summary>
internal sealed class TestLink : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private TestLink(FakeVehicle vehicle, MavlinkConnection connection)
    {
        Vehicle = vehicle;
        Connection = connection;
    }

    public FakeVehicle Vehicle { get; }

    public MavlinkConnection Connection { get; }

    public VehicleSession Session { get; private set; } = null!;

    public static async Task<TestLink> StartAsync(FakeVehicleOptions? options = null, TimeSpan? linkTimeout = null, Action<FakeVehicle>? configure = null)
    {
        var vehicle = new FakeVehicle(options);
        configure?.Invoke(vehicle);
        var connection = MavlinkConnection.Create(vehicle.Transport, new MavlinkConnectionOptions
        {
            LinkTimeout = linkTimeout ?? TimeSpan.FromMilliseconds(600),
            HeartbeatInterval = TimeSpan.FromMilliseconds(200)
        });
        var discovered = new TaskCompletionSource<VehicleSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.VehicleDiscovered += session => discovered.TrySetResult(session);
        await vehicle.StartAsync();
        await connection.StartAsync();
        var link = new TestLink(vehicle, connection) { Session = await discovered.Task.WaitAsync(Timeout) };
        return link;
    }

    public static Dictionary<string, (MavParamType Type, double Value)> Parameters(int count) =>
        Enumerable.Range(0, count).ToDictionary(
            i => $"PARAM_{i:D3}",
            i => i % 2 == 0 ? (MavParamType.Int32, (double)i) : (MavParamType.Real32, i + 0.5));

    public static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Vehicle.DisposeAsync();
    }
}
