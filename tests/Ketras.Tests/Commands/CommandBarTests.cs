using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.App.Commands;
using Ketras.App.Shell;
using Ketras.Mavlink.Messages;
using Ketras.Station.Commands;
using Ketras.Station.Links;

namespace Ketras.Tests.Commands;

[Collection(AvaloniaCollection.Name)]
public sealed class CommandBarTests
{
    private static CommandButtonViewModel Button(CommandBarViewModel bar, string id) => bar.Buttons.Single(b => b.Id == id);

    private static async Task<(AppFixture App, CommandBarViewModel Bar)> ConnectedAsync()
    {
        var app = AppFixture.Create();
        await app.StartAsync();
        await app.Get<ConnectionService>().ConnectAsync(new LinkProfile { Name = "Bench" });
        var bar = app.Get<CommandBarViewModel>();
        await AppFixture.WaitForAsync(() => Button(bar, "arm").IsEnabled);
        return (app, bar);
    }

    [Fact]
    public async Task Buttons_follow_the_catalog_in_groups_and_say_why_they_cannot_run()
    {
        await using var app = AppFixture.Create();
        var bar = app.Get<CommandBarViewModel>();

        Assert.Equal(app.Get<CommandService>().Commands.Select(c => c.Id), bar.Buttons.Select(b => b.Id));
        Assert.False(bar.Buttons[0].StartsGroup);
        Assert.Contains(bar.Buttons, b => b.StartsGroup);
        var arm = Button(bar, "arm");
        Assert.False(arm.IsEnabled);
        Assert.Equal("Arm: no vehicle", arm.Tip);
        Assert.True(arm.NeedsConfirmation);
        Assert.True(Button(bar, "mode-hold").RunsDirectly);
        Assert.Equal("50", Assert.Single(Button(bar, "takeoff").Inputs).Value);
    }

    [Fact]
    public async Task A_confirmed_command_runs_once_and_shows_its_result_then_returns_to_idle()
    {
        var (app, bar) = await ConnectedAsync();
        await using var _ = app;
        var arm = Button(bar, "arm");

        await arm.ConfirmCommand.ExecuteAsync(null);
        await AppFixture.WaitForAsync(() => arm.IsAccepted);

        Assert.True(app.Vehicle.IsArmed);
        Assert.Single(app.Vehicle.Commands, c => c.Command == MavCmd.ComponentArmDisarm);
        await AppFixture.WaitForAsync(() => arm.Feedback == CommandFeedback.Idle);
        await AppFixture.WaitForAsync(() => !Button(bar, "arm").IsEnabled && Button(bar, "disarm").IsEnabled);
    }

    [Fact]
    public async Task Entered_values_go_with_the_command()
    {
        var (app, bar) = await ConnectedAsync();
        await using var _ = app;
        app.Vehicle.SendPosition(47.39, 8.54, 500, 0);
        await Button(bar, "arm").ConfirmCommand.ExecuteAsync(null);
        var takeoff = Button(bar, "takeoff");
        await AppFixture.WaitForAsync(() => takeoff.IsEnabled);

        takeoff.Inputs[0].Value = "80";
        await takeoff.ConfirmCommand.ExecuteAsync(null);

        var sent = Assert.Single(app.Vehicle.Commands, c => c.Command == MavCmd.NavTakeoff);
        Assert.Equal(580f, sent.Params[6]);
    }

    [Fact]
    public async Task A_mode_change_is_accepted_once_the_vehicle_reports_the_mode_and_becomes_the_active_tool()
    {
        var (app, bar) = await ConnectedAsync();
        await using var _ = app;
        var rtl = Button(bar, "mode-rtl");

        var running = rtl.ConfirmCommand.ExecuteAsync(null);
        await running;
        await AppFixture.WaitForAsync(() => rtl.IsAccepted && rtl.IsActiveMode);
        Assert.False(Button(bar, "mode-hold").IsActiveMode);
    }

    [Fact]
    public async Task A_refusal_shows_on_the_button_with_the_vehicles_reason()
    {
        var (app, bar) = await ConnectedAsync();
        await using var _ = app;
        app.Vehicle.ArmingResult = MavResult.Denied;
        var arm = Button(bar, "arm");

        await arm.ConfirmCommand.ExecuteAsync(null);
        await AppFixture.WaitForAsync(() => arm.IsRejected);

        Assert.Equal("Arm: denied", arm.Tip);
    }

    [Fact]
    public async Task Commands_a_source_adds_appear_and_go_away()
    {
        await using var app = AppFixture.Create();
        var bar = app.Get<CommandBarViewModel>();
        var source = new Source();

        using (app.Get<CommandService>().RegisterAction("src.hello", (_, _) => Task.FromResult(CommandResult.Accepted())))
        using (app.Get<CommandService>().AddSource(source))
        {
            var hello = Button(bar, "src.hello");
            Assert.True(hello.StartsGroup);
            Assert.True(hello.IsEnabled);
            await hello.RunCommand.ExecuteAsync(null);
            await AppFixture.WaitForAsync(() => hello.IsAccepted);
        }

        Assert.DoesNotContain(bar.Buttons, b => b.Id == "src.hello");
    }

    [AvaloniaFact]
    public async Task The_bar_sits_on_top_of_the_window_with_one_tool_button_per_command()
    {
        await using var app = AppFixture.Create();
        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var bar = window.GetVisualDescendants().OfType<CommandBarView>().Single();
        var visible = bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("command") && b.IsVisible).ToList();
        Assert.Equal(app.Get<CommandService>().Commands.Count, visible.Count);
        Assert.All(visible, b => Assert.False(b.IsEnabled));
        Assert.True(bar.Bounds.Height > 30);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pressing_confirm_in_the_flyout_sends_the_command_and_closes_the_flyout()
    {
        var (app, _) = await ConnectedAsync();
        await using var __ = app;
        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var bar = window.GetVisualDescendants().OfType<CommandBarView>().Single();
        var arm = bar.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("command") && b.IsVisible && b.DataContext is CommandButtonViewModel { Id: "arm" });
        var flyout = (Flyout)arm.Flyout!;
        flyout.ShowAt(arm);
        Dispatcher.UIThread.RunJobs();
        var confirm = ((Control)flyout.Content!).GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("primary"));

        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(confirm)).Invoke();
        await AppFixture.WaitForAsync(() => app.Vehicle.IsArmed);

        Assert.False(flyout.IsOpen);
        Assert.Single(app.Vehicle.Commands, c => c.Command == MavCmd.ComponentArmDisarm);
        window.Close();
    }

    private sealed class Source : ICommandSource
    {
        public IReadOnlyList<VehicleCommand> Commands { get; } =
            [new VehicleCommand { Id = "src.hello", Label = "Hello", Group = "Source", Target = new StationActionTarget("src.hello"), IsAvailable = _ => true }];

        public event Action? CommandsChanged
        {
            add { }
            remove { }
        }

        public Task<CommandResult?> TryHandleAsync(CommandRequest request, CancellationToken cancellation) =>
            Task.FromResult<CommandResult?>(null);
    }
}
