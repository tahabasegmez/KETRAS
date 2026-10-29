using Ketras.Model.Vehicles;

namespace Ketras.Commands;

/// <summary>
/// One operator command, independent of how a screen shows it. <see cref="Target"/> says where it goes; the
/// <see cref="CommandService"/> routes it. <see cref="Label"/> is English; a screen may translate it by <see cref="Id"/>.
/// </summary>
public sealed record VehicleCommand
{
    /// <summary>Stable identity, unique among all commands; prefix plugin commands with the plugin's name.</summary>
    public required string Id { get; init; }

    /// <summary>Text shown to the operator.</summary>
    public required string Label { get; init; }

    /// <summary>Commands of a group are shown together, in list order.</summary>
    public string Group { get; init; } = "General";

    /// <summary>Name of a Lucide icon (e.g. "Power"); an unknown name shows a placeholder.</summary>
    public string Icon { get; init; } = "CircleQuestionMark";

    /// <summary>Where it goes; null for a command shown but not runnable yet.</summary>
    public CommandTarget? Target { get; init; }

    /// <summary>Fixed parameters, e.g. a ROS message's fields as text.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>Values the operator enters before it is sent; merged into the parameters by key.</summary>
    public IReadOnlyList<CommandInput> Inputs { get; init; } = [];

    /// <summary>Question asked before sending; null sends at once (unless there are inputs).</summary>
    public string? Confirmation { get; init; }

    /// <summary>
    /// Whether it applies in a vehicle state; the argument is null without a vehicle. Null means always, except that an
    /// autopilot command (<see cref="ModeTarget"/>, <see cref="MavlinkTarget"/>) needs a connected vehicle.
    /// </summary>
    public Func<VehicleSnapshot?, bool>? IsAvailable { get; init; }
}

/// <summary>One value the operator enters.</summary>
/// <param name="Key">Parameter it is merged under.</param>
/// <param name="Label">Text shown next to the input.</param>
/// <param name="DefaultValue">Pre-filled value.</param>
public sealed record CommandInput(string Key, string Label, string? DefaultValue = null);

/// <summary>Where a command goes.</summary>
public abstract record CommandTarget;

/// <summary>Switch the autopilot to a mode, by its native name (e.g. "AUTO.RTL" on PX4).</summary>
/// <param name="Mode">Mode name.</param>
public sealed record ModeTarget(string Mode) : CommandTarget;

/// <summary>
/// A MAVLink command (COMMAND_LONG). Parameters MAVLink defines as "NaN = unspecified" should be NaN; reserved ones 0.
/// </summary>
/// <param name="Command">MAV_CMD id, e.g. 400 for COMPONENT_ARM_DISARM.</param>
public sealed record MavlinkTarget(ushort Command) : CommandTarget
{
    /// <summary>Parameters 1–7.</summary>
    public IReadOnlyList<double> Params { get; init; } = [0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// An input holding a height in metres above the vehicle's current altitude; parameter 7 becomes the vehicle's AMSL
    /// altitude plus that height.
    /// </summary>
    public string? RelativeAltitudeInput { get; init; }
}

/// <summary>How a ROS command reaches its system.</summary>
public enum RosOperation
{
    /// <summary>Publish on a topic (no acknowledgement).</summary>
    Publish,

    /// <summary>Call a service and evaluate its response.</summary>
    CallService
}

/// <summary>A topic or service of a vehicle-side ROS 2 system, reached through rosbridge.</summary>
/// <param name="Operation">Publish or call.</param>
/// <param name="Name">Name without the station's namespace prefix, e.g. "/payload/release".</param>
/// <param name="MessageType">Message or service type, e.g. "std_msgs/msg/String".</param>
public sealed record RosTarget(RosOperation Operation, string Name, string MessageType) : CommandTarget;

/// <summary>An action of the station itself, registered with <see cref="CommandService.RegisterAction"/>.</summary>
/// <param name="Action">Registered action name, e.g. "upload-mission".</param>
public sealed record StationActionTarget(string Action) : CommandTarget;

/// <summary>A command the operator triggered.</summary>
/// <param name="Command">The command.</param>
/// <param name="Values">Values the operator entered, by input key.</param>
/// <param name="Vehicle">The vehicle it is for; null for the active one.</param>
public sealed record CommandRequest(VehicleCommand Command, IReadOnlyDictionary<string, string>? Values = null, VehicleId? Vehicle = null)
{
    /// <summary>Fixed parameters merged with the entered values.</summary>
    public IReadOnlyDictionary<string, string> Parameters =>
        (Command.Parameters ?? new Dictionary<string, string>()).Concat(Values ?? new Dictionary<string, string>())
            .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value);
}

/// <summary>How a command ended, in operator-facing words.</summary>
/// <param name="IsAccepted">Accepted, or the action succeeded.</param>
/// <param name="Message">Why it was refused, or what an action did.</param>
/// <param name="IsNoResponse">Refused because the vehicle never answered (as opposed to answering no).</param>
public sealed record CommandResult(bool IsAccepted, string? Message = null, bool IsNoResponse = false)
{
    /// <summary>An accepted result.</summary>
    public static CommandResult Accepted(string? message = null) => new(true, message);

    /// <summary>A refused result with its reason.</summary>
    public static CommandResult Refused(string reason) => new(false, reason);
}

/// <summary>A source of commands other than the defaults, e.g. a plugin for a vehicle-side system.</summary>
public interface ICommandSource
{
    /// <summary>Its commands, in order.</summary>
    IReadOnlyList<VehicleCommand> Commands { get; }

    /// <summary>Raised when <see cref="Commands"/> changes.</summary>
    event Action? CommandsChanged;

    /// <summary>Handles a command itself, or returns null to leave it to routing by target.</summary>
    Task<CommandResult?> TryHandleAsync(CommandRequest request, CancellationToken cancellation);
}
