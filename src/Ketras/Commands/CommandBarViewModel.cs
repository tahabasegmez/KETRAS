using System.Collections.ObjectModel;
using System.Globalization;
using Ketras.Model.Vehicles;
using Ketras.Resources;
using Ketras.Station.Commands;
using Ketras.Station.Links;
using Ketras.Station.Ros;
using Ketras.Station.Vehicles;
using Ketras.Utilities;

namespace Ketras.Commands;

/// <summary>
/// The command bar along the top of the window: every command the station offers (defaults and plugins), grouped,
/// enabled while it can run, with the vehicle's failing checks in the tooltip. A mode change counts as done once the
/// vehicle reports the mode. Results also arrive as
/// toasts from <see cref="CommandService"/>; the bar shows them on the button for a moment.
/// </summary>
public sealed class CommandBarViewModel : IDisposable
{
    private static readonly TimeSpan FeedbackTime = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ModeConfirmTimeout = TimeSpan.FromSeconds(3);

    private readonly CommandService _commands;
    private readonly VehicleRegistry _vehicles;
    private readonly ConnectionService _connections;
    private readonly RosService _ros;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Dictionary<CommandButtonViewModel, ITimer> _timers = [];
    private Vehicle? _vehicle;
    private string? _lastMode;
    private bool? _lastArmed;

    /// <summary>Follows the commands and what decides whether they can run.</summary>
    public CommandBarViewModel(CommandService commands, VehicleRegistry vehicles, ConnectionService connections, RosService ros, IUiDispatcher ui, TimeProvider time)
    {
        _commands = commands;
        _vehicles = vehicles;
        _connections = connections;
        _ros = ros;
        _ui = ui;
        _time = time;
        _commands.CommandsChanged += OnChanged;
        _vehicles.ActiveChanged += OnActiveChanged;
        _connections.StatusChanged += OnLinkChanged;
        if (_ros.Dispatcher is { } dispatcher)
        {
            dispatcher.ConnectionChanged += OnRosChanged;
        }

        Follow(vehicles.Active);
        Rebuild();
    }

    /// <summary>The buttons, in group order.</summary>
    public ObservableCollection<CommandButtonViewModel> Buttons { get; } = [];

    /// <inheritdoc />
    public void Dispose()
    {
        _commands.CommandsChanged -= OnChanged;
        _vehicles.ActiveChanged -= OnActiveChanged;
        _connections.StatusChanged -= OnLinkChanged;
        if (_ros.Dispatcher is { } dispatcher)
        {
            dispatcher.ConnectionChanged -= OnRosChanged;
        }

        Follow(null);
        foreach (var timer in _timers.Values)
        {
            timer.Dispose();
        }
    }

    private void OnChanged() => _ui.Post(Rebuild);

    private void OnLinkChanged(LinkStatus status) => _ui.Post(UpdateAll);

    private void OnRosChanged(bool connected) => _ui.Post(UpdateAll);

    private void OnActiveChanged(Vehicle? vehicle) => _ui.Post(() =>
    {
        Follow(vehicle);
        UpdateAll();
    });

    private void Follow(Vehicle? vehicle)
    {
        if (_vehicle is not null)
        {
            _vehicle.SnapshotChanged -= OnSnapshot;
            _vehicle.HealthChanged -= OnHealth;
        }

        _vehicle = vehicle;
        if (vehicle is not null)
        {
            vehicle.SnapshotChanged += OnSnapshot;
            vehicle.HealthChanged += OnHealth;
        }
    }

    private void OnHealth(Station.Events.HealthReport report) => _ui.Post(UpdateAll);

    /// <summary>Snapshots come ten times a second; the buttons change only when the mode or arming does.</summary>
    private void OnSnapshot(VehicleSnapshot snapshot)
    {
        if (snapshot.Mode == _lastMode && snapshot.IsArmed == _lastArmed)
        {
            return;
        }

        _lastMode = snapshot.Mode;
        _lastArmed = snapshot.IsArmed;
        _ui.Post(UpdateAll);
    }

    private void Rebuild()
    {
        var existing = Buttons.ToDictionary(button => button.Id);
        Buttons.Clear();
        string? group = null;
        foreach (var command in _commands.Commands)
        {
            var startsGroup = group is not null && group != command.Group;
            group = command.Group;
            var button = existing.TryGetValue(command.Id, out var kept) && kept.StartsGroup == startsGroup
                ? kept
                : new CommandButtonViewModel(command, startsGroup, RunAsync);
            Buttons.Add(button);
        }

        UpdateAll();
    }

    private void UpdateAll()
    {
        var commands = _commands.Commands.ToDictionary(command => command.Id);
        var mode = _vehicles.Active?.Snapshot.Mode;
        foreach (var button in Buttons)
        {
            var command = commands.GetValueOrDefault(button.Id, button.Command);
            button.Update(command, _commands.WhyNotRunnable(command), _commands.Advice(command), mode);
        }

        ConfirmModes(mode);
    }

    private async Task RunAsync(CommandButtonViewModel button, IReadOnlyDictionary<string, string>? values)
    {
        if (button.IsPending)
        {
            return;
        }

        StopTimer(button);
        button.Show(CommandFeedback.Pending, Strings.CommandWaiting);
        var result = await _commands.ExecuteAsync(new CommandRequest(button.Command, values));
        _ui.Post(() => Finish(button, result));
    }

    private void Finish(CommandButtonViewModel button, CommandResult result)
    {
        if (!result.IsAccepted)
        {
            Settle(button, result.IsNoResponse ? CommandFeedback.NoResponse : CommandFeedback.Rejected, result.Message);
            return;
        }

        if (button.Command.Target is ModeTarget target && !string.Equals(_vehicles.Active?.Snapshot.Mode, target.Mode, StringComparison.OrdinalIgnoreCase))
        {
            // Accepted, but a mode change is done only when the vehicle reports the mode.
            button.Show(CommandFeedback.Pending, string.Format(CultureInfo.CurrentCulture, Strings.CommandModeWaiting, target.Mode));
            Start(button, ModeConfirmTimeout, () =>
            {
                var reported = _vehicles.Active?.Snapshot.Mode is { Length: > 0 } now ? now : "-";
                Settle(button, CommandFeedback.Rejected, string.Format(CultureInfo.CurrentCulture, Strings.CommandModeNotConfirmed, reported));
            });
            return;
        }

        Settle(button, CommandFeedback.Accepted, result.Message);
    }

    private void ConfirmModes(string? mode)
    {
        foreach (var button in Buttons.Where(b => b.IsPending && b.Command.Target is ModeTarget target
                     && string.Equals(target.Mode, mode, StringComparison.OrdinalIgnoreCase) && _timers.ContainsKey(b)).ToList())
        {
            Settle(button, CommandFeedback.Accepted, null);
        }
    }

    /// <summary>Shows a result for a moment, then the button returns to idle.</summary>
    private void Settle(CommandButtonViewModel button, CommandFeedback feedback, string? detail)
    {
        StopTimer(button);
        button.Show(feedback, detail);
        Start(button, FeedbackTime, () =>
        {
            StopTimer(button);
            button.Show(CommandFeedback.Idle);
        });
    }

    private void Start(CommandButtonViewModel button, TimeSpan after, Action action)
    {
        StopTimer(button);
        _timers[button] = _time.CreateTimer(_ => _ui.Post(action), null, after, Timeout.InfiniteTimeSpan);
    }

    private void StopTimer(CommandButtonViewModel button)
    {
        if (_timers.Remove(button, out var timer))
        {
            timer.Dispose();
        }
    }
}
