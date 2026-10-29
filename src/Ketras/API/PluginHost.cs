using System.Text.Json;
using Ketras.Commands;
using Ketras.Environment;
using Ketras.Notifications;
using Ketras.Planning;
using Ketras.Ros;
using Ketras.Settings;
using Ketras.Vehicles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ketras.API;

/// <summary>
/// Worker that starts the registered plugins, adds their commands
/// and environment providers, and disposes them on shutdown. A plugin that fails to start is logged and left out.
/// </summary>
public sealed class PluginHost(
    IEnumerable<IStationPlugin> registered,
    VehicleRegistry vehicles,
    CommandService commands,
    PlanStore plans,
    EnvironmentStore environment,
    EnvironmentWorker environmentWorker,
    NotificationStore notifications,
    RosService ros,
    IOptions<StationOptions> options,
    ILoggerFactory loggers) : IHostedService
{
    private readonly List<(IStationPlugin Plugin, IDisposable? Commands)> _started = [];
    private readonly ILogger _logger = loggers.CreateLogger<PluginHost>();

    /// <summary>The plugins that started.</summary>
    public IReadOnlyList<IStationPlugin> Plugins
    {
        get
        {
            lock (_started)
            {
                return [.. _started.Select(s => s.Plugin)];
            }
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var plugin in registered.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var settings = options.Value.Plugins.Settings.TryGetValue(plugin.Name, out var block) ? block : default(JsonElement);
            var context = new PluginContext(vehicles, commands, plans, environment, notifications, ros, settings, loggers.CreateLogger($"Plugin.{plugin.Name}"));
            try
            {
                await plugin.StartAsync(context, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Plugin {Name} failed to start", plugin.Name);
                notifications.Notify("Plugins", NotificationSeverity.Error, $"Plugin {plugin.Name} did not start", exception.Message);
                await plugin.DisposeAsync();
                continue;
            }

            var source = plugin is ICommandSource commandSource ? commands.AddSource(commandSource) : null;
            if (plugin is IEnvironmentProvider provider)
            {
                await environmentWorker.AddAsync(provider, cancellationToken);
            }

            lock (_started)
            {
                _started.Add((plugin, source));
            }

            _logger.LogInformation("Plugin {Name} started", plugin.Name);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<(IStationPlugin Plugin, IDisposable? Commands)> started;
        lock (_started)
        {
            started = [.. _started];
            _started.Clear();
        }

        foreach (var (plugin, source) in started)
        {
            source?.Dispose();
            try
            {
                await plugin.DisposeAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Plugin {Name} did not stop cleanly", plugin.Name);
            }
        }
    }
}
