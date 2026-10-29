using Ketras.Geo;
using Ketras.Terrain;
using Ketras.Vehicles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ketras.Planning;

/// <summary>
/// Worker that keeps the plan's planned home: the active vehicle's home once one has reported it, before that the first
/// mission item with a position (normally the takeoff), unless the plan brought its own home (a plan file). The home altitude is the terrain there when it is known (relative
/// altitudes are flown above the ground the vehicle armed on), else the vehicle's reported altitude, else 0.
/// </summary>
public sealed class PlannedHomeWorker(VehicleRegistry vehicles, PlanStore plans, TerrainService terrain, ILogger<PlannedHomeWorker> logger) : IHostedService
{
    private VehicleStatus? _status;
    private volatile bool _hasVehicleHome;
    private GeoPoint? _derived;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        vehicles.ActiveChanged += Follow;
        plans.Changed += OnPlanChanged;
        Follow(vehicles.Active);
        OnPlanChanged(plans.Current, PlanChange.Mission);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        vehicles.ActiveChanged -= Follow;
        plans.Changed -= OnPlanChanged;
        Follow(null);
        return Task.CompletedTask;
    }

    private void Follow(Vehicle? vehicle)
    {
        if (_status is not null)
        {
            _status.HomeChanged -= OnHome;
        }

        _status = vehicle?.Session.Status;
        if (_status is not null)
        {
            _status.HomeChanged += OnHome;
            if (_status.Home is { } home)
            {
                OnHome(home);
            }
        }
    }

    private void OnPlanChanged(Plan plan, PlanChange what)
    {
        var home = plan.Mission.Settings.PlannedHome;
        if (_hasVehicleHome || !what.HasFlag(PlanChange.Mission) || (!home.IsUnset && (_derived is not { } derived || GeoMath.DistanceMeters(home, derived) >= 1))
            || plan.Mission.Items.FirstOrDefault(i => i.UsesLocation && i.Location is not null)?.Location is not { } first)
        {
            return;
        }

        var ground = terrain.IsEnabled ? terrain.GroundAt(first) : null;
        _derived = first;
        SetHome(first, Math.Round(ground ?? 0, 1));
    }

    private void OnHome(HomePosition home)
    {
        _hasVehicleHome = true;
        var point = new GeoPoint(home.Latitude, home.Longitude);
        var ground = terrain.IsEnabled ? terrain.GroundAt(point) : null;
        var altitude = Math.Round(ground ?? home.AltitudeAmsl, 1);
        SetHome(point, altitude);
        logger.LogInformation(ground is null
            ? "Planned home from the vehicle at {Latitude:F6}, {Longitude:F6}, {Altitude:F1} m (no terrain data)"
            : "Planned home from the vehicle at {Latitude:F6}, {Longitude:F6}, {Altitude:F1} m from terrain", home.Latitude, home.Longitude, altitude);
    }

    private void SetHome(GeoPoint point, double altitude) =>
        plans.UpdateSettings(settings =>
            !settings.PlannedHome.IsUnset && GeoMath.DistanceMeters(settings.PlannedHome, point) < 1 && Math.Abs(settings.HomeAltitudeMeters - altitude) < 0.5
                ? settings
                : settings with { PlannedHome = point, HomeAltitudeMeters = altitude });
}
