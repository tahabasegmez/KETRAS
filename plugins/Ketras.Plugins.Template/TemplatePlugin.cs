using System.Text.Json;
using System.Text.Json.Nodes;
using Ketras.Station.Commands;
using Ketras.Station.Plugins;
using Microsoft.Extensions.Logging;

namespace Ketras.Plugins.Template;

/// <summary>
/// Strategy (<see cref="IStationPlugin"/>): the smallest working plugin, to copy for a new vehicle-side system (see
/// README.md). It adds three commands:
/// <list type="bullet">
/// <item><b>Hello</b>: publishes the configured text on a topic (<c>std_msgs/msg/String</c>).</item>
/// <item><b>Lights</b>: calls a <c>std_srvs/srv/SetBool</c> service; the operator chooses on or off and confirms; only while disarmed.</item>
/// <item><b>Report</b>: a station action the plugin registers itself; it reports what the plugin last heard.</item>
/// </list>
/// It listens to one status topic and reads its settings block (Ketras:Plugins:Settings:template).
/// </summary>
public sealed class TemplatePlugin : IStationPlugin, ICommandSource
{
    /// <summary>Plugin name: the key of its settings block.</summary>
    public const string PluginName = "template";

    private const string Group = "Template";
    private const string ReportAction = "template.report";

    private readonly List<IDisposable> _registrations = [];
    private IAsyncDisposable? _subscription;
    private ILogger? _logger;
    private volatile string? _lastStatus;

    /// <inheritdoc />
    public string Name => PluginName;

    /// <inheritdoc />
    public IReadOnlyList<VehicleCommand> Commands { get; private set; } = [];

    /// <inheritdoc />
    public event Action? CommandsChanged;

    /// <inheritdoc />
    public async Task StartAsync(PluginContext context, CancellationToken cancellation)
    {
        if (context.Ros.Dispatcher is not { } ros)
        {
            throw new InvalidOperationException("the template plugin needs ROS: set Ketras:Ros:Enabled in the settings");
        }

        _logger = context.Logger;
        var settings = TemplateSettings.From(context.Settings);

        // A topic this side publishes is advertised once, before the first send. Commands get the namespace prefix on
        // their own; names used directly with the dispatcher need Prefixed.
        ros.Advertise(context.Ros.Prefixed(settings.HelloTopic), "std_msgs/msg/String");
        _subscription = await ros.SubscribeAsync(context.Ros.Prefixed(settings.StatusTopic), "std_msgs/msg/String", OnStatus, throttleMs: 500);
        _registrations.Add(context.Commands.RegisterAction(ReportAction, (_, _) => Task.FromResult(Report())));

        Commands = BuildCommands(settings);
        CommandsChanged?.Invoke();
        _logger.LogInformation("Hello on {Topic}, status from {Status}", settings.HelloTopic, settings.StatusTopic);
    }

    /// <inheritdoc />
    public Task<CommandResult?> TryHandleAsync(CommandRequest request, CancellationToken cancellation) =>
        // Null leaves every command to the station's routing by its target. Return a result here only for a command
        // that needs work the targets cannot express (e.g. several messages in order).
        Task.FromResult<CommandResult?>(null);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _registrations.ForEach(registration => registration.Dispose());
        _registrations.Clear();
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }
    }

    private void OnStatus(JsonNode message) => _lastStatus = message["data"]?.GetValue<string>();

    private CommandResult Report()
    {
        var text = _lastStatus is { } status ? $"last status: {status}" : "no status received yet";
        _logger?.LogInformation("Report: {Text}", text);
        return CommandResult.Accepted(text);
    }

    private static IReadOnlyList<VehicleCommand> BuildCommands(TemplateSettings settings) =>
    [
        new VehicleCommand
        {
            Id = "template.hello",
            Label = "Hello",
            Icon = "MessageSquare",
            Group = Group,
            Target = new RosTarget(RosOperation.Publish, settings.HelloTopic, "std_msgs/msg/String"),
            Parameters = new Dictionary<string, string> { ["data"] = settings.HelloText }
        },
        new VehicleCommand
        {
            Id = "template.lights",
            Label = "Lights",
            Icon = "Lightbulb",
            Group = Group,
            Target = new RosTarget(RosOperation.CallService, settings.LightsService, "std_srvs/srv/SetBool"),
            Inputs = [new CommandInput("data", "On (true) or off (false)", "true")],
            Confirmation = "Switch the lights?",
            IsAvailable = vehicle => vehicle is { IsArmed: false }
        },
        new VehicleCommand
        {
            Id = "template.report",
            Label = "Report",
            Icon = "ClipboardList",
            Group = Group,
            Target = new StationActionTarget(ReportAction),
            IsAvailable = _ => true
        }
    ];

    /// <summary>The plugin's settings block, e.g. <c>"template": { "helloText": "hi" }</c>.</summary>
    private sealed record TemplateSettings
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public string HelloTopic { get; init; } = "/template/hello";

        public string HelloText { get; init; } = "hello from Ketras";

        public string LightsService { get; init; } = "/template/lights";

        public string StatusTopic { get; init; } = "/template/status";

        public static TemplateSettings From(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object ? element.Deserialize<TemplateSettings>(Json) ?? new TemplateSettings() : new TemplateSettings();
    }
}
