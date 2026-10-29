using Ketras.Commands;
using Ketras.Comms;
using Ketras.Environment.Weather;
using Ketras.Notifications;
using Ketras.Planning;
using Ketras.Scene3D;
using Ketras.Settings;
using Ketras.Setup;
using Ketras.Shell;
using Ketras.Simulation.Px4Sitl;
using Ketras.Telemetry;
using Ketras.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ketras;

/// <summary>Registers the application: the station, PX4 SITL, and every screen's ViewModel.</summary>
public static partial class AppServices
{
    /// <summary>
    /// Adds the station and the screens. A <see cref="IUiDispatcher"/> registered before keeps its place (tests run
    /// inline); otherwise Avalonia's UI thread is used.
    /// </summary>
    public static IServiceCollection AddKetrasApp(this IServiceCollection services)
    {
        services.AddKetrasStation();
        services.AddPx4Sitl();
        AddCustom(services);

        services.TryAddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<UnitService>();
        services.TryAddSingleton<IFileDialogService, AvaloniaFileDialogService>();

        services.AddSingleton<LinkStatusViewModel>();
        services.AddSingleton<VehicleStatusViewModel>();
        services.AddSingleton<NotificationCenterViewModel>();
        services.AddSingleton<ToastsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<SetupContext>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<CommandBarViewModel>();
        services.AddSingleton<TelemetryPanelViewModel>();
        services.AddSingleton<FlightBarViewModel>();
        services.AddSingleton<ReadinessViewModel>();
        services.AddSingleton<WeatherViewModel>();
        services.AddSingleton<PlanningViewModel>();
        services.AddSingleton<Scene3DViewModel>();
        services.AddSingleton<ShellViewModel>();
        return services;
    }

    /// <summary>Registers what a custom build adds (its plugins); implemented in the folder named by KetrasCustomDir.</summary>
    static partial void AddCustom(IServiceCollection services);
}
