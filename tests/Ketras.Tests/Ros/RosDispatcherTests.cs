using System.Text.Json.Nodes;
using Ketras.Ros;

namespace Ketras.Tests.Ros;

public sealed class RosDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<RosDispatcher> ConnectAsync(FakeRosbridge server, RosDispatcherOptions? options = null)
    {
        var dispatcher = new RosDispatcher(server.Uri, options ?? new RosDispatcherOptions { ReconnectDelay = TimeSpan.FromMilliseconds(100) });
        dispatcher.Start();
        await WaitForAsync(() => dispatcher.IsConnected);
        return dispatcher;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_text_publish_is_advertised_and_sent_with_the_field_types()
    {
        await using var server = new FakeRosbridge();
        await using var dispatcher = await ConnectAsync(server);

        var result = await dispatcher.SendAsync(new RosRequest(RosInteraction.Publish, "/flag", "std_msgs/msg/Bool",
            new Dictionary<string, string> { ["data"] = "true" }));

        Assert.True(result.IsAccepted);
        await WaitForAsync(() => server.Ops("publish").Any());
        Assert.Contains(server.Ops("advertise"), op => (string?)op["topic"] == "/flag");
        Assert.True(server.Ops("publish").Single()["msg"]!["data"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Nested_fields_are_built_from_dotted_names()
    {
        await using var server = new FakeRosbridge();
        await using var dispatcher = await ConnectAsync(server);

        await dispatcher.SendAsync(new RosRequest(RosInteraction.Publish, "/pose", "geometry_msgs/msg/Pose",
            new Dictionary<string, string> { ["position.x"] = "1.5", ["orientation.w"] = "1" }));

        await WaitForAsync(() => server.Ops("publish").Any());
        var message = server.Ops("publish").Single()["msg"]!;
        Assert.Equal(1.5, message["position"]!["x"]!.GetValue<double>());
        Assert.Equal(1, message["orientation"]!["w"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_bad_value_or_field_fails_without_sending()
    {
        await using var server = new FakeRosbridge();
        await using var dispatcher = await ConnectAsync(server);

        var badBool = await dispatcher.SendAsync(new RosRequest(RosInteraction.Publish, "/flag", "std_msgs/msg/Bool", new Dictionary<string, string> { ["data"] = "maybe" }));
        var badField = await dispatcher.SendAsync(new RosRequest(RosInteraction.Publish, "/flag", "std_msgs/msg/Bool", new Dictionary<string, string> { ["nope"] = "1" }));
        var badType = await dispatcher.SendAsync(new RosRequest(RosInteraction.Publish, "/x", "no_pkg/msg/Nothing"));

        Assert.Equal(RosOutcome.Failed, badBool.Outcome);
        Assert.Equal(RosOutcome.Failed, badField.Outcome);
        Assert.Equal(RosOutcome.Failed, badType.Outcome);
        Assert.Empty(server.Ops("publish"));
    }

    [Fact]
    public async Task A_service_reporting_failure_is_rejected_with_its_message()
    {
        await using var server = new FakeRosbridge();
        server.Services["/lights"] = new JsonObject { ["success"] = false, ["message"] = "busy" };
        await using var dispatcher = await ConnectAsync(server);

        var result = await dispatcher.SendAsync(new RosRequest(RosInteraction.CallService, "/lights", "std_srvs/srv/SetBool",
            new Dictionary<string, string> { ["data"] = "1" }));

        Assert.Equal(RosOutcome.Rejected, result.Outcome);
        Assert.Equal("busy", result.Reason);
        Assert.True(server.Ops("call_service").Last()["args"]!["data"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_silent_service_gives_no_response()
    {
        await using var server = new FakeRosbridge();
        await using var dispatcher = await ConnectAsync(server, new RosDispatcherOptions { ServiceTimeout = TimeSpan.FromMilliseconds(300) });

        var result = await dispatcher.SendJsonAsync(RosInteraction.CallService, "/silent", "std_srvs/srv/Trigger", []);

        Assert.Equal(RosOutcome.NoResponse, result.Outcome);
    }

    [Fact]
    public async Task Handlers_of_one_topic_are_isolated_and_the_last_one_unsubscribes()
    {
        await using var server = new FakeRosbridge();
        await using var dispatcher = await ConnectAsync(server);
        var received = 0;
        var first = await dispatcher.SubscribeAsync("/status", "std_msgs/msg/String", _ => throw new InvalidOperationException("bad handler"));
        var second = await dispatcher.SubscribeAsync("/status", "std_msgs/msg/String", _ => Interlocked.Increment(ref received));
        await WaitForAsync(() => server.Ops("subscribe").Any());

        await server.SendRawAsync("{ not json");
        await server.PublishAsync("/status", new JsonObject { ["data"] = "a" });
        await server.PublishAsync("/status", new JsonObject { ["data"] = "b" });
        await WaitForAsync(() => Volatile.Read(ref received) == 2);

        await first.DisposeAsync();
        Assert.Empty(server.Ops("unsubscribe"));
        await second.DisposeAsync();
        await WaitForAsync(() => server.Ops("unsubscribe").Any());
        Assert.Single(server.Ops("subscribe"));
    }

    [Fact]
    public async Task After_a_dropped_connection_it_reconnects_and_restores_advertisements_and_subscriptions()
    {
        await using var server = new FakeRosbridge();
        await using var dispatcher = await ConnectAsync(server);
        dispatcher.Advertise("/out", "std_msgs/msg/String");
        await using var subscription = await dispatcher.SubscribeAsync("/in", "std_msgs/msg/String", _ => { });
        await WaitForAsync(() => server.Ops("subscribe").Count() == 1 && server.Ops("advertise").Count() == 1);

        await server.DropClientsAsync();
        await WaitForAsync(() => server.Connections == 2 && dispatcher.IsConnected);

        await WaitForAsync(() => server.Ops("subscribe").Count() == 2 && server.Ops("advertise").Count() == 2);
    }
}
