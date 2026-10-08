using Ketras.Mavlink.Messages;
using Ketras.Station.Commands;
using Ketras.Station.Events;
using Ketras.Station.Links;
using Ketras.Station.Notifications;

namespace Ketras.Station.Tests;

public sealed class EventsTests
{
    private static readonly uint NoPosition = 1u << Px4Events.GlobalPosition;

    [Fact]
    public void Px4_events_are_worded_as_libevents_words_them()
    {
        var definitions = Px4Events.Definitions;

        var position = definitions.Interpret(Px4Events.Event(Px4Events.NoGlobalPosition, Px4Events.Error, Px4Events.Mission | Px4Events.Loiter, Px4Events.GlobalPosition))!;
        Assert.Equal(("check_modes_global_pos", "arming_check", "Navigation error: No valid global position estimate"), (position.Name, position.Group, position.Message));
        Assert.Equal(NotificationSeverity.Error, position.Severity);
        Assert.Contains("Mission", EventDefinitions.Display(position.Values[0]), StringComparison.Ordinal);
        Assert.Contains("Loiter", EventDefinitions.Display(position.Values[0]), StringComparison.Ordinal);

        // The developer-profile part of the description is left out; the parameter tag becomes its text.
        var mission = definitions.Interpret(Px4Events.Event(Px4Events.NoMission, Px4Events.Error, Px4Events.Mission, Px4Events.MissionComponent))!;
        Assert.Equal("Upload a mission first.", mission.Description);

        var cpu = definitions.Interpret(Px4Events.Event(Px4Events.CpuLoad, 4, Px4Events.AllModes, (byte)20, 95.5f))!;
        Assert.Equal("CPU load too high: 95.5%", cpu.Message);
        Assert.Equal(NotificationSeverity.Warning, cpu.Severity);

        var mode = definitions.Interpret(Px4Events.Event(Px4Events.ModeSwitchNotPossible, 2, Px4Events.MissionCustomMode, (byte)3))!;
        Assert.Equal("Switching to mode 'Mission' is currently not possible", mode.Message);
        Assert.Equal("append_health_and_arming_messages", mode.Type);

        Assert.Equal(3, definitions.ModeGroupOf(Px4Events.MissionCustomMode));
        Assert.Null(definitions.Interpret(Px4Events.Event(0x05000001, 6)));
        Assert.Throws<FormatException>(() => EventsParser.Parse("{}"));
    }

    [Fact]
    public void A_report_says_which_modes_can_arm_or_run_and_which_checks_fail()
    {
        var assembler = new HealthReportAssembler();
        HealthReport? report = null;
        foreach (var @event in Px4Events.Report(NoPosition, Px4Events.AllModes & ~(Px4Events.Mission | Px4Events.Loiter), Px4Events.AllModes & ~Px4Events.Mission,
                     (Px4Events.NoGlobalPosition, Px4Events.Mission | Px4Events.Loiter, Px4Events.GlobalPosition)))
        {
            report = assembler.Add(Px4Events.Definitions.Interpret(@event)!) ?? report;
        }

        Assert.NotNull(report);
        Assert.False(report.CanArm(3));
        Assert.False(report.CanArm(4));
        Assert.True(report.CanArm(0));
        Assert.False(report.CanRun(3));
        Assert.True(report.CanRun(4));
        Assert.Equal("mission", report.ModeGroups[3].Name);
        var check = Assert.Single(report.ChecksFor(3));
        Assert.Equal(("Navigation error: No valid global position estimate", NotificationSeverity.Error, "Global position estimate"),
            (check.Message, check.Severity, check.Component));
        Assert.Empty(report.ChecksFor(0));
    }

    [Fact]
    public void A_check_outside_a_report_is_ignored()
    {
        var assembler = new HealthReportAssembler();
        var stray = Px4Events.Definitions.Interpret(Px4Events.Event(Px4Events.NoGlobalPosition, Px4Events.Error, Px4Events.Mission, Px4Events.GlobalPosition))!;
        var closing = Px4Events.Definitions.Interpret(Px4Events.Event(Px4Events.HealthSummary, Px4Events.Protocol, (byte)0, 0u, 0u, 0u))!;

        Assert.Null(assembler.Add(stray));
        Assert.Null(assembler.Add(closing));
    }

    [Fact]
    public async Task The_vehicle_is_asked_for_its_report_and_a_failing_check_holds_the_preflight_condition()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: v =>
        {
            Px4Events.Serve(v);
            v.PrearmChecksRequested += () => Px4Events.SendReport(v, Px4Events.Report(NoPosition, Px4Events.AllModes & ~(Px4Events.Mission | Px4Events.Loiter),
                Px4Events.AllModes & ~Px4Events.Mission, (Px4Events.NoGlobalPosition, Px4Events.Mission | Px4Events.Loiter, Px4Events.GlobalPosition)));
        });
        var vehicle = await station.ConnectAsync();
        await StationFixture.WaitForAsync(() => vehicle.Health is not null);

        var store = station.Get<NotificationStore>();
        await StationFixture.WaitForAsync(() => store.Conditions.Any(c => c.Title == "Preflight checks: the vehicle cannot arm"));
        var condition = store.Conditions.Single(c => c.Title == "Preflight checks: the vehicle cannot arm");
        Assert.Equal("Navigation error: No valid global position estimate", condition.Detail);

        // The report is advice: Arm stays runnable, and its tooltip says what fails.
        var commands = station.Get<CommandService>();
        var arm = commands.Commands.Single(c => c.Id == "arm");
        Assert.Null(commands.WhyNotRunnable(arm));
        Assert.Equal("Navigation error: No valid global position estimate", commands.Advice(arm));
        Assert.Equal("Navigation error: No valid global position estimate", commands.Advice(commands.Commands.Single(c => c.Id == "mode-mission")));
        Assert.Null(commands.Advice(commands.Commands.Single(c => c.Id == "mode-rtl")));

        // The checks pass: the condition ends.
        Px4Events.SendReport(station.Vehicle, Px4Events.Report(0, Px4Events.AllModes, Px4Events.AllModes));
        await StationFixture.WaitForAsync(() => store.Conditions.All(c => c.Title != "Preflight checks: the vehicle cannot arm"));
    }

    [Fact]
    public async Task A_refused_arm_says_why_once_and_both_the_request_and_the_answer_are_in_the_link_log()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: v =>
        {
            Px4Events.Serve(v);
            v.ArmingResult = MavResult.Denied;
            v.PrearmChecksRequested += () => Px4Events.SendReport(v, Px4Events.Report(NoPosition, Px4Events.AllModes & ~Px4Events.Loiter, Px4Events.AllModes,
                (Px4Events.NoGlobalPosition, Px4Events.Loiter, Px4Events.GlobalPosition)));
        });
        var vehicle = await station.ConnectAsync();
        await StationFixture.WaitForAsync(() => vehicle.Health is not null);
        var commands = station.Get<CommandService>();

        var running = commands.ExecuteAsync(new CommandRequest(commands.Commands.Single(c => c.Id == "arm")));
        // PX4 says it twice: as STATUSTEXT ending in a tab, and as an event; the text copy is dropped.
        station.Vehicle.SendStatusText(MavSeverity.Critical, "Arming denied: Resolve system health failures first\t");
        station.Vehicle.SendEvent(Px4Events.ArmDenied, 2, []);
        var result = await running;

        Assert.False(result.IsAccepted);
        Assert.Equal("denied; Arming denied: Resolve system health failures first; Navigation error: No valid global position estimate", result.Message);
        var log = station.Get<LinkLogStore>().Entries.Select(e => e.Text).ToList();
        Assert.Contains("Command Arm sent", log);
        Assert.Single(log, text => text.EndsWith("Arming denied: Resolve system health failures first", StringComparison.Ordinal));
        Assert.Contains(log, text => text.StartsWith("Command Arm: refused (denied; ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_mode_the_vehicle_cannot_switch_to_is_explained_with_its_failing_checks()
    {
        await using var station = await StationFixture.StartAsync(vehicleSetup: v =>
        {
            Px4Events.Serve(v);
            v.ModeResult = MavResult.TemporarilyRejected;
            v.PrearmChecksRequested += () => Px4Events.SendReport(v, Px4Events.Report(1u << Px4Events.MissionComponent, Px4Events.AllModes & ~Px4Events.Mission,
                Px4Events.AllModes & ~Px4Events.Mission, (Px4Events.NoMission, Px4Events.Mission, Px4Events.MissionComponent)));
        });
        var vehicle = await station.ConnectAsync();
        await StationFixture.WaitForAsync(() => vehicle.Health is not null);
        var commands = station.Get<CommandService>();

        var running = commands.ExecuteAsync(new CommandRequest(commands.Commands.Single(c => c.Id == "mode-mission")));
        station.Vehicle.SendStatusText(MavSeverity.Critical, "Switching to Mission is currently not available\t");
        station.Vehicle.SendEvent(Px4Events.ModeSwitchNotPossible, 2, Px4Events.Args(Px4Events.MissionCustomMode, (byte)3));
        var result = await running;

        var said = "Switching to mode 'Mission' is currently not possible: " + Px4Events.Definitions
            .Interpret(Px4Events.Event(Px4Events.NoMission, Px4Events.Error, Px4Events.Mission, Px4Events.MissionComponent))!.Message;
        Assert.Equal("temporarily rejected; " + said, result.Message);
        Assert.Contains(station.Get<LinkLogStore>().Entries, e => e.Text.EndsWith(said, StringComparison.Ordinal) && e.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public async Task Without_events_metadata_the_vehicles_text_messages_are_kept_whole()
    {
        await using var station = await StationFixture.StartAsync();
        var vehicle = await station.ConnectAsync();
        var said = new List<string>();
        vehicle.Message += (_, text) => said.Add(text);

        station.Vehicle.SendStatusText(MavSeverity.Critical, "Arming denied: Resolve system health failures first\t");
        await StationFixture.WaitForAsync(() => said.Count == 1);

        Assert.Equal("Arming denied: Resolve system health failures first", said[0]);
        Assert.Null(vehicle.Health);
        Assert.Null(station.Get<CommandService>().Advice(station.Get<CommandService>().Commands.Single(c => c.Id == "arm")));
    }
}
