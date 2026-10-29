using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;

namespace Ketras.Tests.Commands;

public sealed class CommandClientTests
{
    [Fact]
    public async Task A_mode_change_is_acknowledged_and_shows_in_the_heartbeat()
    {
        await using var link = await TestLink.StartAsync();

        var result = await link.Session.Commands.SetModeAsync("AUTO.RTL");

        Assert.True(result.IsAccepted);
        await TestLink.WaitForAsync(() => link.Session.Status.State.Mode == "AUTO.RTL");
    }

    [Fact]
    public async Task An_unknown_mode_is_refused_without_sending_anything()
    {
        await using var link = await TestLink.StartAsync();

        var result = await link.Session.Commands.SetModeAsync("NOT.A.MODE");

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain(link.Vehicle.Commands, c => c.Command == MavCmd.DoSetMode);
    }

    [Fact]
    public async Task A_refusal_carries_the_vehicles_result()
    {
        await using var link = await TestLink.StartAsync(configure: v => v.ArmingResult = MavResult.TemporarilyRejected);

        var result = await link.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(MavResult.TemporarilyRejected, result.Result);
        Assert.Equal("temporarily rejected", result.Reason);
    }

    [Fact]
    public async Task A_state_changing_command_without_an_answer_is_sent_once()
    {
        await using var link = await TestLink.StartAsync(configure: v => v.IgnoreCommands = true);

        var result = await link.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);

        Assert.Equal(CommandOutcome.NoResponse, result.Outcome);
        Assert.Single(link.Vehicle.Commands, c => c.Command == MavCmd.ComponentArmDisarm);
    }

    [Fact]
    public async Task A_status_request_without_an_answer_is_retried_three_times_with_a_rising_confirmation()
    {
        await using var link = await TestLink.StartAsync(configure: v => v.IgnoreCommands = true);
        await Task.Delay(4000); // let the discovery requests (also unanswered) finish

        var result = await link.Session.Commands.RequestMessageAsync(147);

        Assert.Equal(CommandOutcome.NoResponse, result.Outcome);
        var tries = link.Vehicle.Commands.Where(c => c.Command == MavCmd.RequestMessage && (int)c.Params[0] == 147).ToList();
        Assert.Equal([0, 1, 2], tries.Select(c => (int)c.Confirmation));
    }

    [Fact]
    public async Task The_same_command_is_refused_while_its_answer_is_pending()
    {
        await using var link = await TestLink.StartAsync(configure: v => v.IgnoreCommands = true);

        var first = link.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);
        var second = await link.Session.Commands.SendLongAsync(MavCmd.ComponentArmDisarm, 1);

        Assert.Equal(CommandOutcome.Duplicate, second.Outcome);
        Assert.Equal(CommandOutcome.NoResponse, (await first).Outcome);
    }
}
