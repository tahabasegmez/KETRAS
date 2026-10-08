using Ketras.Station.Commands;
using Ketras.Station.Environment;
using Ketras.Station.Events;
using Ketras.Station.Links;
using Ketras.Station.Missions;
using Ketras.Station.Notifications;
using Ketras.Station.Parameters;
using Ketras.Station.Plugins;
using Ketras.Station.Ros;
using Ketras.Station.Settings;
using Ketras.Station.Setup;
using Ketras.Station.Simulation;
using Ketras.Station.Terrain;
using Ketras.Station.Vehicles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ketras.Station;

/// <summary>Registers the station in a service collection; a host (desktop app, script or service) runs it with the generic host.</summary>
public static class StationServiceCollectionExtensions
{
    /// <summary>
    /// Adds every station service. Options come from the <see cref="StationOptions.SectionName"/> configuration section
    /// when the host binds it, and from <paramref name="configure"/>. Files go to <see cref="StationPaths.Default"/> unless
    /// a <see cref="StationPaths"/> is registered first.
    /// </summary>
    public static IServiceCollection AddKetrasStation(this IServiceCollection services, Action<StationOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<StationOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(StationPaths.Default);
        services.TryAddSingleton<ILinkFactory, MavlinkLinkFactory>();

        services.AddSingleton<NotificationStore>();
        services.AddSingleton<LinkLogStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<LinkProfileStore>();
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<VehicleRegistry>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<PendingParameterStore>();
        services.AddSingleton<VehicleSetupService>();
        services.AddSingleton<CalibrationWorkflow>();
        services.AddSingleton<RadioCalibrationWorkflow>();
        services.AddSingleton<ActuatorTestWorkflow>();
        services.AddSingleton<PlanStore>();
        services.AddSingleton<TerrainService>();
        services.AddSingleton<EnvironmentStore>();
        services.AddSingleton<CommandService>();
        services.AddSingleton<MissionProgressTracker>();
        services.AddSingleton<PluginLoader>();

        services.AddSingleton<ManualEnvironmentProvider>();
        services.AddSingleton<ZoneFileEnvironmentProvider>();
        services.AddSingleton<IEnvironmentProvider>(provider => provider.GetRequiredService<ManualEnvironmentProvider>());
        services.AddSingleton<IEnvironmentProvider>(provider => provider.GetRequiredService<ZoneFileEnvironmentProvider>());
        services.AddSingleton<IEnvironmentProvider, VehicleWeatherProvider>();
        services.AddSingleton<IEnvironmentProvider>(provider =>
            provider.GetRequiredService<IOptions<StationOptions>>().Value.Environment.AdsbFromAutopilot
                ? new AdsbEnvironmentProvider(provider.GetRequiredService<ConnectionService>())
                : new DisabledProvider("ADS-B"));

        AddHosted<LinkDiagnosticsWorkflow>(services);
        AddHosted<VehicleEventsWorker>(services);
        AddHosted<LinkNotificationsWorker>(services);
        AddHosted<RosService>(services);
        AddHosted<EnvironmentWorker>(services);
        AddHosted<MissionUploadService>(services);
        AddHosted<PlannedHomeWorker>(services);
        AddHosted<PlanValidationService>(services);
        AddHosted<TerrainMonitorWorker>(services);
        AddHosted<FlightMonitorWorker>(services);
        AddHosted<SimulatorGroundWorker>(services);
        AddHosted<PluginHost>(services);
        return services;
    }

    /// <summary>Registers a plugin built into the application.</summary>
    public static IServiceCollection AddStationPlugin<TPlugin>(this IServiceCollection services) where TPlugin : class, IStationPlugin =>
        services.AddSingleton<IStationPlugin, TPlugin>();

    /// <summary>Registers a simulator that simulator link profiles can start.</summary>
    public static IServiceCollection AddSimulator<TSimulator>(this IServiceCollection services) where TSimulator : class, ISimulator =>
        services.AddSingleton<ISimulator, TSimulator>();

    /// <summary>Registers an environment provider started with the station.</summary>
    public static IServiceCollection AddEnvironmentProvider<TProvider>(this IServiceCollection services) where TProvider : class, IEnvironmentProvider =>
        services.AddSingleton<IEnvironmentProvider, TProvider>();

    /// <summary>One instance that is both a resolvable service and a hosted service.</summary>
    private static void AddHosted<T>(IServiceCollection services) where T : class, IHostedService
    {
        services.AddSingleton<T>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<T>());
    }

    /// <summary>A provider the settings turned off.</summary>
    private sealed class DisabledProvider(string name) : IEnvironmentProvider
    {
        public string Name => name;

        public Task StartAsync(IEnvironmentSink sink, CancellationToken cancellation) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellation) => Task.CompletedTask;
    }
}
