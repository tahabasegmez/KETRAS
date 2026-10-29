using System.Globalization;
using Ketras.Commands;
using Ketras.Mavlink.Messages;
using Ketras.Mavlink.Vehicles;
using Ketras.Model.Geo;
using Ketras.Model.Vehicles;
using Ketras.Notifications;
using Ketras.Planning.Missions;
using Ketras.Settings;
using Ketras.Vehicles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ketras.Terrain;

/// <summary>
/// Worker that watches the active vehicle against the terrain twice a second: height above ground, a prediction along
/// the course over the look-ahead time (with the climb rate), and in mission mode the leg to the current item. It reads
/// cells already on disk only and never commands the vehicle; a predicted conflict is a warning condition. The vehicle's
/// altitude is taken as the ground at its home plus its relative altitude, as PX4 flies it, so a GPS altitude error
/// cancels out.
/// </summary>
public sealed class TerrainMonitorWorker : IHostedService
{
    private const string Source = "Terrain";
    private const string AheadCondition = "ahead";
    private const double GridRadiusMeters = 5000;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly VehicleRegistry _vehicles;
    private readonly TerrainService _terrain;
    private readonly MissionProgressTracker _missions;
    private readonly NotificationStore _notifications;
    private readonly TerrainOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TerrainMonitorWorker> _logger;
    private readonly object _lock = new();
    private Vehicle? _followed;
    private DateTime _last;
    private ElevationGrid? _grid;
    private GeoBounds? _gridBounds;
    private bool _loading;
    private (HomePosition Home, double? Ground)? _homeGround;
    private TerrainClearance? _clearance;

    /// <summary>Creates the worker.</summary>
    public TerrainMonitorWorker(VehicleRegistry vehicles, TerrainService terrain, MissionProgressTracker missions, NotificationStore notifications,
        IOptions<StationOptions> options, TimeProvider time, ILogger<TerrainMonitorWorker> logger)
    {
        _vehicles = vehicles;
        _terrain = terrain;
        _missions = missions;
        _notifications = notifications;
        _options = options.Value.Terrain;
        _time = time;
        _logger = logger;
    }

    /// <summary>The active vehicle's clearance; null before the first estimate.</summary>
    public TerrainClearance? Clearance
    {
        get
        {
            lock (_lock)
            {
                return _clearance;
            }
        }
    }

    /// <summary><see cref="Clearance"/> changed.</summary>
    public event Action<TerrainClearance>? ClearanceChanged;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
        {
            _vehicles.ActiveChanged += Follow;
            Follow(_vehicles.Active);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _vehicles.ActiveChanged -= Follow;
        Follow(null);
        return Task.CompletedTask;
    }

    private void Follow(Vehicle? vehicle)
    {
        if (_followed is not null)
        {
            _followed.Session.Status.PositionChanged -= OnPosition;
        }

        _followed = vehicle;
        if (vehicle is not null)
        {
            vehicle.Session.Status.PositionChanged += OnPosition;
        }
    }

    private void OnPosition(VehiclePosition position)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (now - _last < Interval)
            {
                return;
            }

            _last = now;
        }

        if (_followed is not { } vehicle || GridAround(position) is not { } grid)
        {
            Publish(new TerrainClearance(null, null, "no terrain data here", false));
            return;
        }

        var altitude = HomeGround(vehicle) is { } ground ? ground + position.RelativeAltitude : position.AltitudeAmsl;
        var agl = grid.HeightAt(position.Latitude, position.Longitude) is { } below ? altitude - below : (double?)null;
        double? conflict = null;
        if (position.CourseDegrees is { } course && position.GroundSpeed > 3)
        {
            for (var t = 1; t <= (int)_options.LookAhead.TotalSeconds; t++)
            {
                var ahead = GeoMath.Offset(new GeoPoint(position.Latitude, position.Longitude), course, position.GroundSpeed * t);
                if (grid.HeightAt(ahead.Latitude, ahead.Longitude) is { } h && h > altitude + (position.ClimbRate * t))
                {
                    conflict = t;
                    break;
                }
            }
        }

        var leg = CurrentLegConflict(vehicle, position, altitude, grid);
        Publish(new TerrainClearance(agl, conflict, leg ?? (agl is null ? "no terrain data here" : null), conflict is not null || leg is not null));
    }

    private void Publish(TerrainClearance clearance)
    {
        TerrainClearance? previous;
        lock (_lock)
        {
            previous = _clearance;
            _clearance = clearance;
        }

        ClearanceChanged?.Invoke(clearance);
        if (previous?.IsWarning == clearance.IsWarning)
        {
            return;
        }

        if (clearance.IsWarning)
        {
            var detail = string.Join(" ", new[]
            {
                clearance.ConflictAheadSeconds is { } s ? string.Create(CultureInfo.InvariantCulture, $"Predicted contact in {s:F0} s.") : null,
                clearance.Message
            }.Where(text => text is not null));
            _logger.LogWarning("Terrain ahead: {Detail}", detail);
            _notifications.SetCondition(Source, AheadCondition, NotificationSeverity.Warning, "Terrain ahead", detail);
        }
        else
        {
            _notifications.ClearCondition(Source, AheadCondition);
        }
    }

    private double? HomeGround(Vehicle vehicle)
    {
        if (vehicle.Session.Status.Home is not { } home)
        {
            return null;
        }

        lock (_lock)
        {
            if (_homeGround is { } cached && cached.Home == home)
            {
                return cached.Ground;
            }
        }

        var ground = _terrain.GroundAt(new GeoPoint(home.Latitude, home.Longitude));
        lock (_lock)
        {
            _homeGround = (home, ground);
        }

        return ground;
    }

    /// <summary>In mission mode, the straight leg from the vehicle to the item it flies to, sampled every 30 m.</summary>
    private string? CurrentLegConflict(Vehicle vehicle, VehiclePosition position, double altitude, ElevationGrid grid)
    {
        var status = vehicle.Session.Status;
        var sequence = status.MissionCurrent;
        if (_missions.Uploaded(vehicle.Id) is not { } uploaded || sequence < 0 || sequence >= uploaded.Items.Count || status.Home is not { } home
            || !string.Equals(status.State.Mode, AutopilotModes.For(vehicle.Session.Profile.Name).Mission, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = uploaded.Items[sequence];
        if (target.Frame is not (MavFrame.GlobalRelativeAlt or MavFrame.Global) || (target.X == 0 && target.Y == 0))
        {
            return null;
        }

        var from = new GeoPoint(position.Latitude, position.Longitude);
        var to = new GeoPoint(target.X / 1e7, target.Y / 1e7);
        var targetAltitude = target.Frame == MavFrame.GlobalRelativeAlt ? (HomeGround(vehicle) ?? home.AltitudeAmsl) + target.Z : target.Z;
        var distance = GeoMath.DistanceMeters(from, to);
        var bearing = GeoMath.BearingDegrees(from, to);
        var samples = (int)Math.Ceiling(distance / 30);
        for (var k = 1; k <= samples; k++)
        {
            var x = distance * k / samples;
            var point = GeoMath.Offset(from, bearing, x);
            if (grid.HeightAt(point.Latitude, point.Longitude) is { } h && h > altitude + ((targetAltitude - altitude) * x / distance))
            {
                return string.Create(CultureInfo.InvariantCulture, $"The current leg meets terrain in {x:F0} m.");
            }
        }

        return null;
    }

    /// <summary>A grid of ±5 km around the vehicle from disk, reloaded in the background near its edge.</summary>
    private ElevationGrid? GridAround(VehiclePosition position)
    {
        lock (_lock)
        {
            var inner = _gridBounds?.Expand(-GridRadiusMeters / 2);
            if (_grid is not null && inner is { } box && box.Contains(position.Latitude, position.Longitude))
            {
                return _grid;
            }

            if (_loading)
            {
                return _grid;
            }

            _loading = true;
        }

        var bounds = new GeoBounds(position.Latitude, position.Longitude, position.Latitude, position.Longitude).Expand(GridRadiusMeters);
        _ = Task.Run(() =>
        {
            ElevationGrid? grid = null;
            try
            {
                grid = _terrain.LoadGrid(bounds);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                _logger.LogWarning(exception, "Terrain around the vehicle could not be read");
            }

            lock (_lock)
            {
                _grid = grid;
                _gridBounds = bounds;
                _loading = false;
            }
        });

        lock (_lock)
        {
            return _grid;
        }
    }
}
