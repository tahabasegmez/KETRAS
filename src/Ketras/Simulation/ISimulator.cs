using Ketras.Comms;
using Ketras.Geo;

namespace Ketras.Simulation;

/// <summary>
/// Strategy: a simulator the station can start and stop on the operator's request (e.g. PX4 SITL). It only runs the
/// simulator; the link to it is an ordinary <see cref="LinkProfile"/> (<see cref="Link"/>) the connection service opens.
/// </summary>
public interface ISimulator : IAsyncDisposable
{
    /// <summary>What can be chosen before a start.</summary>
    SimulatorDescription Description { get; }

    /// <summary>Where the simulator stands.</summary>
    SimulatorStatus Status { get; }

    /// <summary>The link to the running simulator; null while it is not running.</summary>
    LinkProfile? Link { get; }

    /// <summary>Raised on any thread when <see cref="Status"/> or <see cref="Link"/> changes.</summary>
    event Action<SimulatorStatus>? StatusChanged;

    /// <summary>One line of the simulator's output, raised on any thread.</summary>
    event Action<string, SimulatorOutputLevel>? Output;

    /// <summary>Looks for a simulator started outside the station, without starting anything.</summary>
    Task RefreshAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Whether this machine can run the simulator for <paramref name="launch"/>: each prerequisite, ready or with the
    /// fix, and an action the simulator can take itself (e.g. build its environment). Changes nothing. None by default.
    /// </summary>
    Task<IReadOnlyList<SimulatorCheck>> CheckAsync(SimulatorLaunch launch, CancellationToken cancellation = default) =>
        Task.FromResult<IReadOnlyList<SimulatorCheck>>([]);

    /// <summary>
    /// Runs the action of a check (<see cref="SimulatorCheck.Action"/>) for <paramref name="launch"/>; the status is
    /// <see cref="SimulatorRunState.Preparing"/> meanwhile, and <see cref="StopAsync"/> cancels it.
    /// </summary>
    Task RunActionAsync(string actionId, SimulatorLaunch launch, CancellationToken cancellation = default) =>
        Task.FromException(new NotSupportedException($"{Description.Name} has no action '{actionId}'."));

    /// <summary>Starts the simulator; returns once it is launched, and <see cref="Status"/> follows it to Running.</summary>
    Task StartAsync(SimulatorLaunch launch, CancellationToken cancellation = default);

    /// <summary>Opens the simulator's window while <see cref="SimulatorStatus.Window"/> allows it; nothing by default.</summary>
    Task OpenWindowAsync() => Task.CompletedTask;

    /// <summary>Stops it, or cancels what it is preparing.</summary>
    Task StopAsync();
}

/// <summary>How a prerequisite stands.</summary>
public enum CheckState
{
    /// <summary>Present.</summary>
    Ready,

    /// <summary>Missing; <see cref="SimulatorCheck.Fix"/> or <see cref="SimulatorCheck.Action"/> says how to get it.</summary>
    Missing,

    /// <summary>Usable with a limitation.</summary>
    Warning,

    /// <summary>Not looked at (an earlier check failed).</summary>
    NotChecked
}

/// <summary>Something the simulator can do about a check itself, e.g. build its environment.</summary>
/// <param name="Id">Passed back to <see cref="ISimulator.RunActionAsync"/>.</param>
/// <param name="Label">The button's text, e.g. "Build environment".</param>
/// <param name="IsDestructive">It removes something (e.g. the environment); shown as such.</param>
public sealed record SimulatorAction(string Id, string Label, bool IsDestructive = false);

/// <summary>One prerequisite of a simulator.</summary>
/// <param name="Title">What it is, e.g. "Docker engine".</param>
/// <param name="State">How it stands.</param>
/// <param name="Detail">What was found.</param>
/// <param name="Fix">The command or step that fixes it; null when there is nothing to do.</param>
/// <param name="Required">A start cannot work without it.</param>
/// <param name="Action">What the simulator can do about it itself; null when nothing.</param>
public sealed record SimulatorCheck(string Title, CheckState State, string Detail, string? Fix = null, bool Required = true, SimulatorAction? Action = null)
{
    /// <summary>It stops a start.</summary>
    public bool Blocks => Required && State == CheckState.Missing;
}

/// <summary>Where a simulator stands.</summary>
public enum SimulatorRunState
{
    /// <summary>Not running.</summary>
    Stopped,

    /// <summary>Preparing its environment (e.g. building it); it cannot start meanwhile.</summary>
    Preparing,

    /// <summary>Launched, not ready for a link yet.</summary>
    Starting,

    /// <summary>Ready; <see cref="ISimulator.Link"/> reaches it.</summary>
    Running,

    /// <summary>Shutting down.</summary>
    Stopping
}

/// <summary>Whether the simulator's window (e.g. the Gazebo GUI) can be opened now.</summary>
/// <param name="CanOpen">It can be opened.</param>
/// <param name="IsOpen">It is open.</param>
/// <param name="Reason">Why it cannot be opened now; null when it can.</param>
public sealed record SimulatorWindow(bool CanOpen, bool IsOpen, string? Reason = null);

/// <summary>A simulator's state, what it is doing in a few words, the last error, and its window.</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Detail">What it is doing, e.g. "building the terrain world".</param>
/// <param name="Error">Why it failed.</param>
/// <param name="Window">Its window; null when it has none.</param>
public sealed record SimulatorStatus(SimulatorRunState State, string? Detail = null, string? Error = null, SimulatorWindow? Window = null)
{
    /// <summary>The <see cref="SimulatorWorld.Id"/> it runs while starting or running; null when not known.</summary>
    public string? WorldId { get; init; }

    /// <summary>Not running, no error.</summary>
    public static SimulatorStatus Stopped { get; } = new(SimulatorRunState.Stopped);
}

/// <summary>Kind of an output line.</summary>
public enum SimulatorOutputLevel
{
    /// <summary>Ordinary output.</summary>
    Info,

    /// <summary>A milestone, e.g. ready.</summary>
    Success,

    /// <summary>A warning.</summary>
    Warning,

    /// <summary>An error.</summary>
    Error
}

/// <summary>A world the simulator can run; a <see cref="FixedHome"/> world carries its own home.</summary>
/// <param name="Id">Identifier passed back in <see cref="SimulatorLaunch.WorldId"/>.</param>
/// <param name="Name">Name shown to the operator.</param>
/// <param name="Description">What the world is.</param>
/// <param name="FixedHome">The home cannot be chosen.</param>
public sealed record SimulatorWorld(string Id, string Name, string Description, bool FixedHome)
{
    /// <summary>
    /// The ground follows the real terrain at the home (a DEM-built world). False for a flat world: its ground is level
    /// with the home everywhere, so the station plans against flat ground while it flies there.
    /// </summary>
    public bool HasTerrain { get; init; } = true;
}

/// <summary>One value of a choice option.</summary>
/// <param name="Value">Passed back in <see cref="SimulatorLaunch.Options"/>.</param>
/// <param name="Label">Shown to the operator.</param>
public sealed record SimulatorChoice(string Value, string Label);

/// <summary>
/// A run option beyond the world and the home (e.g. the simulation speed): a choice when <see cref="Choices"/> is
/// set, a number from <see cref="Minimum"/> to <see cref="Maximum"/> otherwise.
/// </summary>
/// <param name="Key">Key in <see cref="SimulatorLaunch.Options"/>.</param>
/// <param name="Label">Shown to the operator.</param>
/// <param name="Help">What it does, for the operator.</param>
/// <param name="Default">The value used when none is given (invariant culture for numbers).</param>
public sealed record SimulatorOption(string Key, string Label, string Help, string Default)
{
    /// <summary>The values to choose from; null for a number.</summary>
    public IReadOnlyList<SimulatorChoice>? Choices { get; init; }

    /// <summary>The smallest number allowed.</summary>
    public double Minimum { get; init; }

    /// <summary>The largest number allowed.</summary>
    public double Maximum { get; init; } = double.MaxValue;

    /// <summary>The number's unit, e.g. "m/s"; empty when none.</summary>
    public string Unit { get; init; } = string.Empty;
}

/// <summary>What a simulator offers before a start.</summary>
public sealed record SimulatorDescription
{
    /// <summary>Identifies it in simulator link profiles.</summary>
    public required string Id { get; init; }

    /// <summary>Name shown to the operator, e.g. "PX4 SITL".</summary>
    public required string Name { get; init; }

    /// <summary>What it runs, e.g. "fixed-wing in Gazebo".</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The worlds to choose from; the first is the default.</summary>
    public required IReadOnlyList<SimulatorWorld> Worlds { get; init; }

    /// <summary>What its window is called, e.g. "Gazebo"; null when it has none.</summary>
    public string? WindowName { get; init; }

    /// <summary>The run options beyond the world and the home.</summary>
    public IReadOnlyList<SimulatorOption> Options { get; init; } = [];

    /// <summary>The default home.</summary>
    public GeoPoint DefaultHome { get; init; }
}

/// <summary>What the operator chose for a start; the home altitude is the simulator's to find (e.g. from terrain).</summary>
/// <param name="WorldId">World to run.</param>
/// <param name="Home">Home position.</param>
public sealed record SimulatorLaunch(string WorldId, GeoPoint Home)
{
    /// <summary>The run options by <see cref="SimulatorOption.Key"/>; a missing one takes its default.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>The value of <paramref name="option"/>, or its default.</summary>
    public string ValueOf(SimulatorOption option) => Options.TryGetValue(option.Key, out var value) ? value : option.Default;
}
