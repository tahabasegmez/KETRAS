using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ketras.Commands;
using Ketras.Comms;
using Ketras.Model.Geo;
using Ketras.Notifications;
using Ketras.Shell;
using Ketras.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ketras.Tests.Comms;

[Collection(AvaloniaCollection.Name)]
public sealed class ConnectionTests
{
    [Fact]
    public void A_choice_option_is_never_checked_as_a_number_and_a_number_out_of_range_says_so()
    {
        var changes = 0;
        var version = new SimulatorOptionViewModel(new SimulatorOption("version", "PX4 version", "help", "v1.17.0")
        {
            Choices = [new Ketras.Simulation.SimulatorChoice("v1.17.0", "v1.17.0"), new Ketras.Simulation.SimulatorChoice("v1.16.0", "v1.16.0")]
        }, _ => changes++);
        Assert.Equal(string.Empty, version.Error);
        changes = 0;
        version.SelectedChoice = version.Choices[1];
        Assert.Equal(("v1.16.0", string.Empty, 1), (version.Value, version.Error, changes));

        var speed = new SimulatorOptionViewModel(new SimulatorOption("speed", "Wind", "help", "0") { Minimum = 0, Maximum = 30, Unit = "m/s" }, _ => { });
        speed.Text = "45";
        Assert.NotEmpty(speed.Error);
        Assert.Equal("0", speed.Value);
    }

    [Fact]
    public async Task The_editor_checks_its_fields_and_saves_new_and_renamed_links()
    {
        await using var app = AppFixture.Create();
        var links = app.Get<ConnectionViewModel>().Links;

        links.NewLinkCommand.Execute(null);
        var editor = links.Editor!;
        Assert.Equal("Link 1", editor.Name);
        editor.Name = " ";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter a name.", editor.Error);

        editor.Name = "Bench";
        editor.UdpPort = "70000";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter a port from 1 to 65535.", editor.Error);

        editor.SelectedKind = editor.Kinds.Single(k => k.Kind == LinkKind.Tcp);
        Assert.True(editor.IsTcp);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter the TCP host.", editor.Error);

        editor.TcpHost = "10.0.0.2";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(links.Editor);
        var row = Assert.Single(links.Profiles);
        Assert.Equal(("Bench", "TCP", "tcp://10.0.0.2:5760"), (row.Name, row.KindLabel, row.Summary));

        links.NewLinkCommand.Execute(null);
        links.Editor!.Name = "bench";
        await links.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("A link named bench exists already.", links.Editor.Error);
        links.Editor.CancelCommand.Execute(null);

        links.EditCommand.Execute(row);
        links.Editor!.Name = "Bench 2";
        await links.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["Bench 2"], links.Profiles.Select(p => p.Name));
        Assert.Equal("10.0.0.2", app.Get<LinkProfileStore>().Profiles.Single().Host);
    }

    [Fact]
    public async Task Connecting_shows_the_status_page_its_steps_details_and_log()
    {
        await using var app = AppFixture.Create();
        await app.StartAsync();
        var screen = app.Get<ConnectionViewModel>();
        app.Get<LinkProfileStore>().Save(new LinkProfile { Name = "Bench" });
        screen.SelectedPage = screen.Pages[1];

        await screen.Links.ConnectCommand.ExecuteAsync(screen.Links.Profiles.Single());
        await AppFixture.WaitForAsync(() => screen.Status.Steps.Count == 4 && screen.Status.Steps.All(s => s.State == LinkStepState.Done) && screen.Status.Messages != "--");

        Assert.Same(screen.Pages[0], screen.SelectedPage);
        Assert.Equal(("Connected · Bench", "udp://:14550", "Disconnect"), (screen.Status.Heading, screen.Status.Summary, screen.Status.DisconnectLabel));
        Assert.Equal("CONNECTED", screen.Status.StepsTitle);
        Assert.Equal(("1 / 1", "PX4", "Fixed wing"), (screen.Status.Identity, screen.Status.Autopilot, screen.Status.VehicleType));
        Assert.True(screen.Links.Profiles.Single().IsInUse);
        Assert.Contains(screen.Log.Rows, row => row is { Text: "Connected to Bench", Level: NotificationSeverity.Success });
        Assert.Contains(screen.Log.Rows, row => row.Text.StartsWith("Parameters:", StringComparison.Ordinal));

        screen.Status.DisconnectCommand.Execute(null);
        await AppFixture.WaitForAsync(() => screen.Status.State == LinkState.Disconnected);
        Assert.Equal("--", screen.Status.Identity);
        Assert.Contains(screen.Log.Rows, row => row.Text == "Disconnected");
    }

    [Fact]
    public async Task Cutting_the_link_to_an_armed_vehicle_asks_first()
    {
        await using var app = AppFixture.Create();
        await app.StartAsync();
        var screen = app.Get<ConnectionViewModel>();
        await screen.ConnectAsync(new LinkProfile { Name = "Bench" });
        await AppFixture.WaitForAsync(() => screen.Status.IsConnected);
        var commands = app.Get<CommandService>();
        await commands.ExecuteAsync(new CommandRequest(commands.Commands.Single(c => c.Id == "arm")));
        await AppFixture.WaitForAsync(() => app.Get<Ketras.Vehicles.VehicleRegistry>().Active!.Snapshot.IsArmed);

        screen.Status.DisconnectCommand.Execute(null);
        Assert.True(screen.Status.IsConfirming);
        Assert.False(screen.Status.ShowHint);
        screen.Status.CancelConfirmCommand.Execute(null);
        Assert.True(screen.Status.IsConnected);

        await screen.ConnectAsync(new LinkProfile { Name = "Other", Port = 14551 });
        Assert.Equal("The vehicle is armed. Switching to Other closes the current link first.", screen.Status.ConfirmText);
        screen.Status.CancelConfirmCommand.Execute(null);

        screen.Status.DisconnectCommand.Execute(null);
        await screen.Status.ConfirmCommand.ExecuteAsync(null);
        await AppFixture.WaitForAsync(() => screen.Status.State == LinkState.Disconnected);
    }

    [Fact]
    public async Task The_sitl_page_starts_with_the_chosen_world_home_and_options_and_logs_the_output()
    {
        var simulator = new FakeSimulator();
        await using var app = WithOnly(simulator);
        var screen = app.Get<ConnectionViewModel>();
        var view = screen.Simulators.Simulators.Single(s => s.Name == "Fake sim");

        view.Latitude = "91";
        await view.StartCommand.ExecuteAsync(null);
        Assert.NotEmpty(view.HomeError);
        Assert.Null(simulator.Launch);

        view.Latitude = "47.5";
        view.Longitude = "8.25";
        var speed = view.Options.Single(o => o.Key == "speed");
        var wind = view.Options.Single(o => o.Key == "wind");
        Assert.Equal(("Simulation speed", "How fast time runs.", true), (speed.Label, speed.Help, speed.IsChoice));
        speed.SelectedChoice = speed.Choices[1];
        wind.Text = "25";
        Assert.Equal("Enter 0…20 m/s", wind.Error);
        Assert.Equal("0", wind.Value);
        wind.Text = "7,5";
        Assert.Empty(wind.Error);
        await view.StartCommand.ExecuteAsync(null);

        Assert.Equal(("flat", new GeoPoint(47.5, 8.25)), (simulator.Launch!.WorldId, simulator.Launch.Home));
        Assert.Equal(new Dictionary<string, string> { ["speed"] = "2", ["wind"] = "7.5" }, simulator.Launch.Options);
        Assert.True(view.CanStop);
        Assert.Equal("Starting", view.StateText);
        Assert.Contains(screen.Log.Rows, row => row is { Text: "Fake sim: booting", Level: NotificationSeverity.Warning });

        simulator.Set(new SimulatorStatus(SimulatorRunState.Stopped, Error: "crashed"));
        Assert.Equal(("crashed", true), (view.Detail, view.HasError));
        Assert.True(view.CanStart);
    }

    [Fact]
    public async Task Opening_the_sitl_page_checks_once_and_a_missing_item_keeps_start_disabled_until_fixed()
    {
        var simulator = new FakeSimulator { Checks = [new("Engine", CheckState.Missing, "Not running.", "start it"), new("Window", CheckState.Warning, "No display.", Required: false)] };
        await using var app = WithOnly(simulator);
        var screen = app.Get<ConnectionViewModel>();
        var view = screen.Simulators.Simulators.Single(s => s.Name == "Fake sim");
        Assert.Empty(view.Checks);

        screen.SelectedPage = screen.Pages[2];
        await AppFixture.WaitForAsync(() => !view.IsChecking && view.Checks.Count == 2);

        Assert.Equal((1, 1), (simulator.Refreshes, simulator.CheckCount));
        Assert.True(view.IsBlocked);
        Assert.False(view.StartCommand.CanExecute(null));
        Assert.Equal(("start it", true, true), (view.Checks[0].Fix, view.Checks[0].IsMissing, view.Checks[0].Blocks));
        Assert.Equal((false, true), (view.Checks[1].Blocks, view.Checks[1].IsWarning));

        screen.SelectedPage = screen.Pages[0];
        screen.SelectedPage = screen.Pages[2];
        Assert.Equal(1, simulator.Refreshes);

        // A choice may change what the machine needs, so it checks again; a number does not.
        view.Options.Single(o => o.Key == "wind").Text = "3";
        Assert.Equal(1, simulator.CheckCount);
        view.Options.Single(o => o.Key == "speed").SelectedChoice = view.Options.Single(o => o.Key == "speed").Choices[1];
        await AppFixture.WaitForAsync(() => simulator.CheckCount == 2 && !view.IsChecking);

        simulator.Checks = [new("Engine", CheckState.Ready, "Running.")];
        await view.CheckAgainCommand.ExecuteAsync(null);
        await AppFixture.WaitForAsync(() => !view.IsChecking);
        Assert.Equal(2, simulator.Refreshes);
        Assert.False(view.IsBlocked);
        Assert.True(view.CanStart);
    }

    [Fact]
    public async Task A_check_action_runs_while_stopped_shows_preparing_and_checks_again_after()
    {
        var simulator = new FakeSimulator { Checks = [new("Environment", CheckState.Missing, "Not built.", Action: new SimulatorAction("build", "Build environment"))] };
        await using var app = WithOnly(simulator);
        var screen = app.Get<ConnectionViewModel>();
        var view = screen.Simulators.Simulators.Single(s => s.Name == "Fake sim");
        await view.OpenAsync();
        await AppFixture.WaitForAsync(() => view.Checks.Count == 1 && !view.IsChecking);
        var row = view.Checks.Single();
        Assert.Equal(("Build environment", true, false), (row.ActionLabel, row.HasAction, row.IsActionDestructive));

        simulator.Checks = [new("Environment", CheckState.Ready, "Built.", Action: new SimulatorAction("remove", "Remove", IsDestructive: true))];
        simulator.OnAction = () =>
        {
            simulator.Set(new SimulatorStatus(SimulatorRunState.Preparing, "building"));
            Assert.Equal(("Preparing", "Cancel", true), (view.StateText, view.StopText, view.CanStop));
            Assert.False(view.CanRunAction);
            Assert.False(view.CanStart);
            simulator.Set(SimulatorStatus.Stopped);
        };
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)row.ActionCommand).ExecuteAsync(null);
        await AppFixture.WaitForAsync(() => !view.IsChecking && view.Checks.Single().IsReady);

        Assert.Equal(["build"], simulator.Actions);
        Assert.True(view.Checks.Single().IsActionDestructive);
    }

    [Fact]
    public async Task The_window_button_follows_what_the_simulator_allows()
    {
        var simulator = new FakeSimulator();
        await using var app = WithOnly(simulator);
        var view = app.Get<ConnectionViewModel>().Simulators.Simulators.Single(s => s.Name == "Fake sim");
        Assert.Equal(("Open Gazebo", false), (view.OpenWindowText, view.HasWindow));

        simulator.Set(new SimulatorStatus(SimulatorRunState.Running, Window: new SimulatorWindow(false, false, "Gazebo is starting.")));
        Assert.True(view.HasWindow);
        Assert.False(view.OpenWindowCommand.CanExecute(null));
        Assert.Equal("Gazebo is starting.", view.WindowHint);

        simulator.Set(new SimulatorStatus(SimulatorRunState.Running, Window: new SimulatorWindow(true, false)));
        Assert.True(view.OpenWindowCommand.CanExecute(null));
        await view.OpenWindowCommand.ExecuteAsync(null);
        Assert.Equal(1, simulator.WindowsOpened);
    }

    [AvaloniaFact]
    public async Task The_sitl_page_lays_out_status_environment_and_options_with_help_marks()
    {
        var simulator = new FakeSimulator { Checks = [new("Engine", CheckState.Missing, "Not running.", "start it")] };
        await using var app = WithOnly(simulator);
        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        app.Get<ShellViewModel>().Show(ShellViewModel.Link);
        var screen = app.Get<ConnectionViewModel>();
        screen.SelectedPage = screen.Pages[2];
        var view = screen.Simulators.Simulators.Single(s => s.Name == "Fake sim");
        await AppFixture.WaitForAsync(() => view.Checks.Count == 1 && !view.IsChecking);
        Pump(window);

        var page = window.GetVisualDescendants().OfType<SimulatorsPageView>().Single();
        Button Named(string name) => page.GetVisualDescendants().OfType<Button>().Last(b => b.Name == name);
        Assert.True(Named("Start").IsVisible);
        Assert.False(Named("Start").IsEffectivelyEnabled);
        Assert.Contains(page.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "start it");

        // World, home, the simulator's two options and the environment section each carry a "?" with their help.
        var marks = page.GetVisualDescendants().OfType<Ketras.Controls.HelpMark>().ToList();
        Assert.Equal(5, marks.Count);
        Assert.All(marks, mark => Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(mark) as string)));
        Assert.Contains(marks, mark => (string?)ToolTip.GetTip(mark) == "How fast time runs.");

        simulator.Set(new SimulatorStatus(SimulatorRunState.Running, Window: new SimulatorWindow(false, false, "Gazebo is starting.")));
        Pump(window);
        Assert.False(page.GetVisualDescendants().OfType<StackPanel>().Last(p => p.Name == "RunOptions").IsEffectivelyEnabled);
        Assert.True(Named("OpenWindow").IsVisible);
        Assert.False(Named("OpenWindow").IsEffectivelyEnabled);
        window.Close();
    }
    [AvaloniaFact]
    public async Task The_vehicle_link_screen_lays_out_its_pages_and_shows_editor_errors()
    {
        await using var app = AppFixture.Create();
        var window = new MainWindow { DataContext = app.Get<ShellViewModel>() };
        window.Show();
        app.Get<ShellViewModel>().Show(ShellViewModel.Link);
        Pump(window);

        var pages = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PageList");
        Assert.Equal(3, pages.ItemCount);
        Assert.Equal("Not connected", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Heading").Text);
        Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Disconnect").IsVisible);

        var screen = app.Get<ConnectionViewModel>();
        screen.SelectedPage = screen.Pages[1];
        screen.Links.NewLinkCommand.Execute(null);
        screen.Links.Editor!.Name = string.Empty;
        await screen.Links.Editor.SaveCommand.ExecuteAsync(null);
        Pump(window);

        var error = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "EditorError");
        Assert.True(error.IsVisible);
        Assert.Equal("Enter a name.", error.GetVisualDescendants().OfType<TextBlock>().Single().Text);

        screen.HideLogCommand.Execute(null);
        Pump(window);
        Assert.False(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LogPane").IsVisible);
        window.Close();
    }

    /// <summary>The app with <paramref name="simulator"/> as its only simulator: opening the SITL page must not probe this machine.</summary>
    private static AppFixture WithOnly(ISimulator simulator) => AppFixture.Create(register: services =>
    {
        services.RemoveAll<ISimulator>();
        services.AddSingleton(simulator);
    });

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class FakeSimulator : ISimulator
    {
        public SimulatorLaunch? Launch { get; private set; }

        public SimulatorDescription Description { get; } = new()
        {
            Id = "fake", Name = "Fake sim", Worlds = [new SimulatorWorld("flat", "Flat", "A flat world.", false)], DefaultHome = new GeoPoint(47, 8),
            WindowName = "Gazebo",
            Options =
            [
                new("speed", "Simulation speed", "How fast time runs.", "1") { Choices = [new("1", "1×"), new("2", "2×")] },
                new("wind", "Wind speed", "Steady wind.", "0") { Minimum = 0, Maximum = 20, Unit = "m/s" }
            ]
        };

        public SimulatorStatus Status { get; private set; } = SimulatorStatus.Stopped;

        public LinkProfile? Link => null;

        public event Action<SimulatorStatus>? StatusChanged;

        public event Action<string, SimulatorOutputLevel>? Output;

        public IReadOnlyList<SimulatorCheck> Checks { get; set; } = [];

        public int Refreshes { get; private set; }

        public int CheckCount { get; private set; }

        public int WindowsOpened { get; private set; }

        public List<string> Actions { get; } = [];

        public Action? OnAction { get; set; }

        public Task RefreshAsync(CancellationToken cancellation = default)
        {
            Refreshes++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SimulatorCheck>> CheckAsync(SimulatorLaunch launch, CancellationToken cancellation = default)
        {
            CheckCount++;
            return Task.FromResult(Checks);
        }

        public Task RunActionAsync(string actionId, SimulatorLaunch launch, CancellationToken cancellation = default)
        {
            Actions.Add(actionId);
            OnAction?.Invoke();
            return Task.CompletedTask;
        }

        public Task StartAsync(SimulatorLaunch launch, CancellationToken cancellation = default)
        {
            Launch = launch;
            Set(new SimulatorStatus(SimulatorRunState.Starting, "booting"));
            Output?.Invoke("booting", SimulatorOutputLevel.Warning);
            return Task.CompletedTask;
        }

        public Task OpenWindowAsync()
        {
            WindowsOpened++;
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;

        public void Set(SimulatorStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(status);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}