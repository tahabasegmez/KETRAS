using System.Collections.Concurrent;
using System.Globalization;
using Ketras.Comms;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Protocols;
using Ketras.Notifications;
using Ketras.Ros;
using Ketras.Rosbridge;
using Ketras.Vehicles;
using Microsoft.Extensions.Logging;

namespace Ketras.Commands;

/// <summary>
/// The command catalog (the defaults for the active vehicle's autopilot plus every source's commands) and their
/// routing: a source that handles a command itself goes first, then the target decides: a mode through the autopilot
/// profile, a MAVLink command as COMMAND_LONG, a ROS target through rosbridge (with the namespace prefix), a station
/// action through its registration. Every request is answered and nothing is resent; the request and its outcome go to
/// the link log, and the outcome is raised as a notification. A vehicle's refusal carries what the vehicle said while
/// answering and, when it reports its health and arming checks, the checks that fail for that mode. Thread-safe.
/// </summary>
public sealed class CommandService
{
    private const string Source = "Commands";
    private const ushort ComponentArmDisarm = 400;

    /// <summary>How long vehicle messages are still collected after a refusal, as they may follow the answer.</summary>
    private static readonly TimeSpan RefusalMessageWindow = TimeSpan.FromMilliseconds(500);

    private readonly VehicleRegistry _vehicles;
    private readonly RosService _ros;
    private readonly NotificationStore _notifications;
    private readonly LinkLogStore _log;
    private readonly ILogger<CommandService> _logger;
    private readonly object _lock = new();
    private readonly List<ICommandSource> _sources = [];
    private readonly Dictionary<string, Func<CommandRequest, CancellationToken, Task<CommandResult>>> _actions = new(StringComparer.Ordinal);

    /// <summary>Creates the service; the catalog follows the active vehicle.</summary>
    public CommandService(VehicleRegistry vehicles, RosService ros, NotificationStore notifications, LinkLogStore log, ILogger<CommandService> logger)
    {
        _vehicles = vehicles;
        _ros = ros;
        _notifications = notifications;
        _log = log;
        _logger = logger;
        vehicles.ActiveChanged += _ => RaiseChanged();
    }

    /// <summary>The catalog or a command's runnability changed.</summary>
    public event Action? CommandsChanged;

    /// <summary>Every command, defaults first; a later command with an id already used is left out.</summary>
    public IReadOnlyList<VehicleCommand> Commands
    {
        get
        {
            List<ICommandSource> sources;
            lock (_lock)
            {
                sources = [.. _sources];
            }

            var profile = _vehicles.Active?.Session.Profile.Name ?? "auto";
            return [.. DefaultCommands.For(profile).Concat(sources.SelectMany(source => source.Commands)).DistinctBy(command => command.Id)];
        }
    }

    /// <summary>Adds a source of commands (e.g. a plugin); disposing the result removes it.</summary>
    public IDisposable AddSource(ICommandSource source)
    {
        lock (_lock)
        {
            _sources.Add(source);
        }

        source.CommandsChanged += RaiseChanged;
        RaiseChanged();
        return new Registration(() =>
        {
            source.CommandsChanged -= RaiseChanged;
            lock (_lock)
            {
                _sources.Remove(source);
            }

            RaiseChanged();
        });
    }

    /// <summary>Registers a station action a <see cref="StationActionTarget"/> runs, replacing one of the same name; disposing the result removes it.</summary>
    public IDisposable RegisterAction(string name, Func<CommandRequest, CancellationToken, Task<CommandResult>> action)
    {
        lock (_lock)
        {
            _actions[name] = action;
        }

        RaiseChanged();
        return new Registration(() =>
        {
            lock (_lock)
            {
                if (_actions.TryGetValue(name, out var current) && current == action)
                {
                    _actions.Remove(name);
                }
            }

            RaiseChanged();
        });
    }

    /// <summary>
    /// Why the command cannot run now, or null when it can: a target, its station action registered, rosbridge connected
    /// for a ROS target, a connected vehicle for an autopilot target, and <see cref="VehicleCommand.IsAvailable"/>.
    /// </summary>
    public string? WhyNotRunnable(VehicleCommand command)
    {
        var vehicle = _vehicles.Active;
        switch (command.Target)
        {
            case null:
                return "not available yet";
            case StationActionTarget action:
                lock (_lock)
                {
                    if (!_actions.ContainsKey(action.Action))
                    {
                        return $"no station action '{action.Action}'";
                    }
                }

                break;
            case RosTarget when _ros.Dispatcher is null:
                return "ROS is disabled in the settings";
            case RosTarget when _ros.Dispatcher is { IsConnected: false }:
                return "not connected to rosbridge";
            case ModeTarget or MavlinkTarget when vehicle is not { IsConnected: true }:
                return "no vehicle";
        }

        var snapshot = vehicle?.Snapshot;
        return command.IsAvailable is { } available
            ? available(snapshot) ? null : "not available in the vehicle's state"
            : command.Target is ModeTarget or MavlinkTarget && snapshot is not { IsConnected: true } ? "no vehicle" : null;
    }

    /// <summary>
    /// What the vehicle's health and arming report says against the command, while it can still be sent: the checks
    /// that stop arming in the current mode, or switching to the command's mode. Null when nothing is known against it.
    /// </summary>
    public string? Advice(VehicleCommand command)
    {
        if (_vehicles.Active is not { Health: { } health } vehicle)
        {
            return null;
        }

        var (group, possible) = command.Target switch
        {
            MavlinkTarget { Command: ComponentArmDisarm } arm when arm.Params.FirstOrDefault() >= 0.5
                && vehicle.ModeGroupOf(vehicle.Snapshot.Mode) is { } current => (current, health.CanArm(current)),
            ModeTarget mode when vehicle.ModeGroupOf(mode.Mode) is { } target => (target, health.CanRun(target)),
            _ => (-1, true)
        };
        if (possible)
        {
            return null;
        }

        var failing = health.ChecksFor(group).Where(check => check.Severity == NotificationSeverity.Error).Select(check => check.Message).ToList();
        return failing.Count > 0 ? string.Join("; ", failing) : "the vehicle's checks do not allow it now";
    }

    /// <summary>Runs a command. Never throws.</summary>
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellation = default)
    {
        var command = request.Command;
        var vehicle = command.Target is ModeTarget or MavlinkTarget ? request.Vehicle is { } id ? _vehicles.Find(id) : _vehicles.Active : null;
        var said = new ConcurrentQueue<string>();
        void Collect(NotificationSeverity severity, string text)
        {
            if (severity >= NotificationSeverity.Warning)
            {
                said.Enqueue(text);
            }
        }

        _log.Append(NotificationSeverity.Info, $"Command {command.Label} sent");
        if (vehicle is not null)
        {
            vehicle.Message += Collect;
        }

        CommandResult result;
        try
        {
            result = await RouteAsync(request, cancellation);
            if (!result.IsAccepted && vehicle is not null)
            {
                await Task.Delay(RefusalMessageWindow, CancellationToken.None);
                result = result with { Message = Explain(result.Message, [.. said], Advice(command)) };
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            _logger.LogError(exception, "Command {Id} failed", command.Id);
            result = CommandResult.Refused(exception.Message);
        }
        finally
        {
            if (vehicle is not null)
            {
                vehicle.Message -= Collect;
            }
        }

        _log.Append(result.IsAccepted ? NotificationSeverity.Success : NotificationSeverity.Error,
            $"Command {command.Label}: {(result.IsAccepted ? "accepted" : "refused")}{(string.IsNullOrEmpty(result.Message) ? string.Empty : " (" + result.Message + ")")}");
        _logger.LogInformation("Command {Label} ({Id}): {Outcome}", command.Label, command.Id, result.IsAccepted ? "accepted" : $"refused: {result.Message}");
        var isAction = command.Target is StationActionTarget;
        _notifications.Notify(Source, result.IsAccepted ? NotificationSeverity.Success : NotificationSeverity.Error,
            $"{command.Label} {(result.IsAccepted ? isAction ? "done" : "accepted" : isAction ? "failed" : "refused")}",
            isAction || !result.IsAccepted ? result.Message : null);
        return result;
    }

    /// <summary>The vehicle's answer, then what it said while answering, then the failing checks it had not said.</summary>
    private static string Explain(string? answer, IReadOnlyList<string> said, string? checks)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(answer))
        {
            parts.Add(answer);
        }

        parts.AddRange(said.Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(checks) && !said.Any(text => text.Contains(checks, StringComparison.Ordinal)))
        {
            parts.Add(checks);
        }

        return string.Join("; ", parts);
    }

    private async Task<CommandResult> RouteAsync(CommandRequest request, CancellationToken cancellation)
    {
        List<ICommandSource> sources;
        lock (_lock)
        {
            sources = [.. _sources];
        }

        foreach (var source in sources)
        {
            if (await source.TryHandleAsync(request, cancellation) is { } handled)
            {
                return handled;
            }
        }

        var vehicle = request.Vehicle is { } id ? _vehicles.Find(id) : _vehicles.Active;
        return request.Command.Target switch
        {
            ModeTarget mode when vehicle is not null => Describe(await vehicle.Session.Commands.SetModeAsync(mode.Mode, cancellation)),
            MavlinkTarget mavlink when vehicle is not null => await SendMavlinkAsync(vehicle, mavlink, request, cancellation),
            ModeTarget or MavlinkTarget => CommandResult.Refused("no vehicle"),
            RosTarget ros => await SendRosAsync(ros, request),
            StationActionTarget action => await RunActionAsync(action, request, cancellation),
            _ => CommandResult.Refused($"'{request.Command.Label}' cannot run now")
        };
    }

    private static async Task<CommandResult> SendMavlinkAsync(Vehicle vehicle, MavlinkTarget target, CommandRequest request, CancellationToken cancellation)
    {
        var p = target.Params.Concat(Enumerable.Repeat(0.0, 7)).Take(7).ToArray();
        if (target.RelativeAltitudeInput is { } key)
        {
            if (!request.Parameters.TryGetValue(key, out var text)
                || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var height) || !double.IsFinite(height) || height <= 0)
            {
                return CommandResult.Refused("the height must be a positive number of metres");
            }

            if (vehicle.Session.Status.Position is not { } position)
            {
                return CommandResult.Refused("the vehicle's position is not known");
            }

            p[6] = position.AltitudeAmsl + height;
        }

        return Describe(await vehicle.Session.Commands.SendLongAsync((MavCmd)target.Command,
            (float)p[0], (float)p[1], (float)p[2], (float)p[3], (float)p[4], (float)p[5], (float)p[6], cancellation: cancellation));
    }

    private async Task<CommandResult> SendRosAsync(RosTarget target, CommandRequest request)
    {
        if (_ros.Dispatcher is not { } dispatcher)
        {
            return CommandResult.Refused("ROS is disabled in the settings");
        }

        var interaction = target.Operation == RosOperation.CallService ? RosInteraction.CallService : RosInteraction.Publish;
        var result = await dispatcher.SendAsync(new RosRequest(interaction, _ros.Prefixed(target.Name), target.MessageType, request.Parameters));
        return new CommandResult(result.IsAccepted, result.Reason.Length > 0 ? result.Reason : null);
    }

    private async Task<CommandResult> RunActionAsync(StationActionTarget target, CommandRequest request, CancellationToken cancellation)
    {
        Func<CommandRequest, CancellationToken, Task<CommandResult>>? action;
        lock (_lock)
        {
            action = _actions.GetValueOrDefault(target.Action);
        }

        return action is null ? CommandResult.Refused($"no station action '{target.Action}'") : await action(request, cancellation);
    }

    /// <summary>Operator-facing text of a MAVLink command result.</summary>
    public static CommandResult Describe(Mavlink.Protocols.CommandResult result) => new(result.IsAccepted, result.Outcome switch
    {
        CommandOutcome.Accepted => null,
        CommandOutcome.NoResponse => "no response from the vehicle",
        CommandOutcome.Duplicate => "the same command is still waiting for an answer",
        _ when result.Reason.Length > 0 => result.Reason,
        _ => result.Outcome.ToString()
    }, result.Outcome == CommandOutcome.NoResponse);

    private void RaiseChanged() => CommandsChanged?.Invoke();

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
