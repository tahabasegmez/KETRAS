using Ketras.Mavlink.Messages;
using Ketras.Station.Commands;
using Ketras.Station.Vehicles;

namespace Ketras.Station.Tests;

public sealed class CommandTests
{
    private static VehicleCommand Command(CommandService commands, string id) => commands.Commands.Single(c => c.Id == id);

    [Fact]
    public async Task Arm_and_mode_commands_reach_the_vehicle_and_report_the_outcome()
    {
        await using var station = await StationFixture.StartAsync();
        await station.ConnectAsync();
        var commands = station.Get<CommandService>();

        var arm = await commands.ExecuteAsync(new CommandRequest(Command(commands, "arm")));
        var rtl = await commands.ExecuteAsync(new CommandRequest(Command(commands, "mode-rtl")));

        Assert.True(arm.IsAccepted);
        Assert.True(rtl.IsAccepted);
        Assert.True(station.Vehicle.IsArmed);
        await StationFixture.WaitForAsync(() => station.Get<VehicleRegistry>().Active!.Snapshot.Mode == "AUTO.RTL");
        Assert.Contains(station.Get<Notifications.NotificationStore>().History, n => n.Title == "Arm accepted");
    }

    [Fact]
    public async Task A_refusal_carries_the_vehicles_reason_and_nothing_is_resent()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: v => v.ArmingResult = MavResult.Denied);
        await station.ConnectAsync();
        var commands = station.Get<CommandService>();

        var result = await commands.ExecuteAsync(new CommandRequest(Command(commands, "arm")));

        Assert.False(result.IsAccepted);
        Assert.Equal("denied", result.Message);
        Assert.Single(station.Vehicle.Commands, c => c.Command == MavCmd.ComponentArmDisarm);
    }

    [Fact]
    public async Task Takeoff_sends_the_entered_height_above_the_vehicles_altitude_as_px4_expects()
    {
        await using var station = await StationFixture.StartAsync();
        await station.ConnectAsync();
        station.Vehicle.SendPosition(47.39, 8.54, 488.5, 0);
        await StationFixture.WaitForAsync(() => station.Get<VehicleRegistry>().Active!.Session.Status.Position is not null);
        var commands = station.Get<CommandService>();

        await commands.ExecuteAsync(new CommandRequest(Command(commands, "takeoff"), new Dictionary<string, string> { [DefaultCommands.TakeoffHeightInput] = "60" }));
        var bad = await commands.ExecuteAsync(new CommandRequest(Command(commands, "takeoff"), new Dictionary<string, string> { [DefaultCommands.TakeoffHeightInput] = "-5" }));

        var sent = Assert.Single(station.Vehicle.Commands, c => c.Command == MavCmd.NavTakeoff);
        Assert.Equal(548.5f, sent.Params[6]);
        Assert.True(float.IsNaN(sent.Params[0]) && float.IsNaN(sent.Params[3]));
        Assert.False(bad.IsAccepted);
    }

    [Fact]
    public async Task Runnability_follows_the_vehicle_ros_and_registered_actions()
    {
        await using var station = await StationFixture.StartAsync();
        var commands = station.Get<CommandService>();
        var ros = new VehicleCommand { Id = "ros", Label = "ROS", Target = new RosTarget(RosOperation.Publish, "/x", "std_msgs/msg/String") };
        var action = new VehicleCommand { Id = "act", Label = "Act", Target = new StationActionTarget("test-action") };

        Assert.Equal("no vehicle", commands.WhyNotRunnable(Command(commands, "mode-hold")));
        Assert.Equal("ROS is disabled in the settings", commands.WhyNotRunnable(ros));
        Assert.Equal("no station action 'test-action'", commands.WhyNotRunnable(action));
        Assert.Equal("ROS is disabled in the settings", (await commands.ExecuteAsync(new CommandRequest(ros))).Message);

        using (commands.RegisterAction("test-action", (request, _) => Task.FromResult(CommandResult.Accepted($"ran {request.Command.Id}"))))
        {
            Assert.Null(commands.WhyNotRunnable(action));
            Assert.Equal("ran act", (await commands.ExecuteAsync(new CommandRequest(action))).Message);
        }

        Assert.NotNull(commands.WhyNotRunnable(action));
        await station.ConnectAsync();
        Assert.Null(commands.WhyNotRunnable(Command(commands, "mode-hold")));
        Assert.Null(commands.WhyNotRunnable(Command(commands, "arm")));
        Assert.NotNull(commands.WhyNotRunnable(Command(commands, "disarm")));
    }

    [Fact]
    public async Task A_command_source_adds_commands_and_may_handle_them_itself()
    {
        await using var station = await StationFixture.StartAsync();
        var commands = station.Get<CommandService>();
        var source = new Source();

        using (commands.AddSource(source))
        {
            Assert.Contains(commands.Commands, c => c.Id == "src-hello");
            Assert.Equal("handled", (await commands.ExecuteAsync(new CommandRequest(source.Commands[0]))).Message);
        }

        Assert.DoesNotContain(commands.Commands, c => c.Id == "src-hello");
    }

    private sealed class Source : ICommandSource
    {
        public IReadOnlyList<VehicleCommand> Commands { get; } = [new VehicleCommand { Id = "src-hello", Label = "Hello" }];

        public event Action? CommandsChanged
        {
            add { }
            remove { }
        }

        public Task<CommandResult?> TryHandleAsync(CommandRequest request, CancellationToken cancellation) =>
            Task.FromResult<CommandResult?>(request.Command.Id == "src-hello" ? CommandResult.Accepted("handled") : null);
    }
}
