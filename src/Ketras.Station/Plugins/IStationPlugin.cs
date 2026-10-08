using System.Text.Json;
using Ketras.Station.Commands;
using Ketras.Station.Environment;
using Ketras.Station.Missions;
using Ketras.Station.Notifications;
using Ketras.Station.Ros;
using Ketras.Station.Vehicles;
using Microsoft.Extensions.Logging;

namespace Ketras.Station.Plugins;

/// <summary>
/// Strategy: an extension for one vehicle-side system or deployment, kept out of the station itself. A plugin may also
/// implement <see cref="ICommandSource"/> (its commands are added to the catalog) and <see cref="IEnvironmentProvider"/>
/// (it reports environment data). Register it at build time with <c>AddStationPlugin&lt;T&gt;()</c>, or drop it into the
/// plugins folder and list its name in the settings.
/// </summary>
public interface IStationPlugin : IAsyncDisposable
{
    /// <summary>Unique name, as listed in the settings; also the key of its settings block.</summary>
    string Name { get; }

    /// <summary>Starts the plugin; throw when it cannot work (the station logs it and goes on without it).</summary>
    Task StartAsync(PluginContext context, CancellationToken cancellation);
}

/// <summary>What a plugin gets from the station.</summary>
/// <param name="Vehicles">The vehicles and the active one.</param>
/// <param name="Commands">Command routing and station actions.</param>
/// <param name="Plans">The plan being edited.</param>
/// <param name="Environment">Environment data from every provider.</param>
/// <param name="Notifications">Messages for the operator.</param>
/// <param name="Ros">rosbridge (its dispatcher is null when ROS is disabled).</param>
/// <param name="Settings">The plugin's own settings block (Ketras:Plugins:Settings:&lt;name&gt;); undefined when absent.</param>
/// <param name="Logger">A logger named after the plugin.</param>
public sealed record PluginContext(
    VehicleRegistry Vehicles,
    CommandService Commands,
    PlanStore Plans,
    EnvironmentStore Environment,
    NotificationStore Notifications,
    RosService Ros,
    JsonElement Settings,
    ILogger Logger);
