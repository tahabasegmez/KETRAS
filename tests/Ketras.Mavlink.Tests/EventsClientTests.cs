using System.Collections.Concurrent;
using Ketras.Mavlink.Protocols;

namespace Ketras.Mavlink.Tests;

public sealed class EventsClientTests
{
    private static async Task<(TestLink Link, ConcurrentQueue<VehicleEvent> Received)> StartAsync()
    {
        var link = await TestLink.StartAsync();
        var received = new ConcurrentQueue<VehicleEvent>();
        link.Session.Events.Received += received.Enqueue;
        return (link, received);
    }

    [Fact]
    public async Task Events_arrive_in_order_with_their_arguments_and_levels()
    {
        var (link, received) = await StartAsync();
        await using var _ = link;

        link.Vehicle.SendEvent(0x01_00_00_2A, 0x64, [7, 1, 2]);
        link.Vehicle.SendEvent(0x01_00_00_2B, 0x63, []);
        await TestLink.WaitForAsync(() => received.Count == 2);

        var first = received.First();
        Assert.Equal(0x01_00_00_2Au, first.Id);
        Assert.Equal(4, first.ExternalLogLevel);
        Assert.Equal(40, first.Arguments.Length);
        Assert.Equal([7, 1, 2], first.Arguments[..3]);
        Assert.Equal([0x01_00_00_2Au, 0x01_00_00_2Bu], received.Select(e => e.Id));
        Assert.True(link.Session.Events.IsSupported);
    }

    [Fact]
    public async Task A_missed_event_is_requested_and_delivered_before_the_later_ones()
    {
        var (link, received) = await StartAsync();
        await using var _ = link;
        link.Vehicle.SendEvent(1, 6, []);
        await TestLink.WaitForAsync(() => received.Count == 1);

        link.Vehicle.DropNextEvents = 1;
        link.Vehicle.SendEvent(2, 6, []);
        link.Vehicle.SendEvent(3, 6, []);
        await TestLink.WaitForAsync(() => received.Count == 3);

        Assert.Equal([1u, 2u, 3u], received.Select(e => e.Id));
        Assert.Contains((ushort)1, link.Vehicle.EventRequests);
    }

    [Fact]
    public async Task A_dropped_last_event_is_fetched_after_the_current_sequence_announces_it()
    {
        var (link, received) = await StartAsync();
        await using var _ = link;
        link.Vehicle.SendEvent(1, 6, []);
        await TestLink.WaitForAsync(() => received.Count == 1);

        link.Vehicle.DropNextEvents = 1;
        link.Vehicle.SendEvent(2, 6, []);

        // The heartbeat loop announces the sequence; the station asks for the missing event.
        await TestLink.WaitForAsync(() => received.Count == 2);
        Assert.Equal(2u, received.Last().Id);
    }

    [Fact]
    public async Task Events_the_vehicle_no_longer_has_are_counted_as_lost_and_the_rest_follow()
    {
        var (link, received) = await StartAsync();
        await using var _ = link;
        var lost = 0;
        link.Session.Events.Lost += count => Interlocked.Add(ref lost, count);
        link.Vehicle.SendEvent(1, 6, []);
        await TestLink.WaitForAsync(() => received.Count == 1);

        link.Vehicle.DropNextEvents = 2;
        link.Vehicle.SendEvent(2, 6, []);
        link.Vehicle.SendEvent(3, 6, []);
        link.Vehicle.ForgetEventsBefore(2);
        link.Vehicle.SendEvent(4, 6, []);

        await TestLink.WaitForAsync(() => received.Count == 3);
        Assert.Equal([1u, 3u, 4u], received.Select(e => e.Id));
        Assert.Equal(1, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task Duplicates_are_dropped()
    {
        var (link, received) = await StartAsync();
        await using var _ = link;
        link.Vehicle.SendEvent(1, 6, []);
        link.Vehicle.SendEvent(2, 6, []);
        await TestLink.WaitForAsync(() => received.Count == 2);

        // A vehicle may repeat an event; it is delivered once.
        link.Vehicle.ResendEvent(1);
        link.Vehicle.ResendEvent(0);
        await Task.Delay(300);

        Assert.Equal(2, received.Count);
    }
}
