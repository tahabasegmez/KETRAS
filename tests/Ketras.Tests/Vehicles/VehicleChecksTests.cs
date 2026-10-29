using Ketras.Commands;
using Ketras.Comms;
using Ketras.Mavlink.Messages;
using Ketras.Setup;
using Ketras.Vehicles;

namespace Ketras.Tests.Vehicles;

/// <summary>The vehicle's health and arming checks and messages, as the screens show them.</summary>
[Collection(AvaloniaCollection.Name)]
public sealed class VehicleChecksTests
{
    private const string NoPosition = "Navigation error: No valid global position estimate";

    private static async Task<AppFixture> ConnectedAsync()
    {
        var app = AppFixture.Create(vehicleSetup: v =>
        {
            Px4Events.Serve(v);
            v.ArmingResult = MavResult.Denied;
            v.PrearmChecksRequested += () => Px4Events.SendReport(v, Px4Events.Report(1u << Px4Events.GlobalPosition, Px4Events.AllModes & ~Px4Events.Loiter,
                Px4Events.AllModes, (Px4Events.NoGlobalPosition, Px4Events.Loiter, Px4Events.GlobalPosition)));
        });
        await app.StartAsync();
        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench" });
        await AppFixture.WaitForAsync(() => app.Get<VehicleRegistry>().Active?.Health is not null);
        return app;
    }

    [Fact]
    public async Task Arm_stays_pressable_and_its_tooltip_names_the_failing_check()
    {
        await using var app = await ConnectedAsync();
        var arm = app.Get<CommandBarViewModel>().Buttons.Single(b => b.Id == "arm");

        await AppFixture.WaitForAsync(() => arm.Tip == $"Arm: {NoPosition}");
        Assert.True(arm.IsEnabled);
    }

    [Fact]
    public async Task The_link_log_shows_the_vehicles_messages_and_each_command_with_its_answer()
    {
        await using var app = await ConnectedAsync();
        var screen = app.Get<ConnectionViewModel>();
        var arm = app.Get<CommandBarViewModel>().Buttons.Single(b => b.Id == "arm");

        app.Vehicle.SendStatusText(MavSeverity.Info, "Takeoff detected");
        await arm.ConfirmCommand.ExecuteAsync(null);

        await AppFixture.WaitForAsync(() => screen.Log.Rows.Any(r => r.Text.StartsWith("Command Arm: refused (denied; ", StringComparison.Ordinal)));
        var texts = screen.Log.Rows.Select(r => r.Text).ToList();
        Assert.Contains(texts, t => t.EndsWith(": Takeoff detected", StringComparison.Ordinal));
        Assert.Contains("Command Arm sent", texts);
        Assert.Contains(texts, t => t.Contains(NoPosition, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_summary_lists_the_failing_checks_and_the_modes_they_stop()
    {
        await using var app = await ConnectedAsync();
        var summary = app.Get<SetupViewModel>().Summary;

        await AppFixture.WaitForAsync(() => summary.Health.Count == 1);
        var row = summary.Health[0];
        Assert.Equal((NoPosition, true), (row.Message, row.IsError));
        Assert.Equal("Global position estimate · stops Loiter", row.Detail);
        Assert.False(summary.IsHealthy);

        Px4Events.SendReport(app.Vehicle, Px4Events.Report(0, Px4Events.AllModes, Px4Events.AllModes));
        await AppFixture.WaitForAsync(() => summary.IsHealthy);
    }
}
