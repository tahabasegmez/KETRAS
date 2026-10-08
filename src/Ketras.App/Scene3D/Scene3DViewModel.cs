using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.App.Common;
using Ketras.App.Resources;
using Ketras.App.Telemetry;
using Ketras.Model.Environment;
using Ketras.Model.Planning;
using Ketras.Model.Terrain;
using Ketras.Model.Vehicles;
using Ketras.Station.Environment;
using Ketras.Station.Missions;
using Ketras.Station.Terrain;
using Ketras.Station.Vehicles;

namespace Ketras.App.Scene3D;

/// <summary>One aircraft of the traffic list.</summary>
/// <param name="Id">Its id, as the scene knows it.</param>
/// <param name="Name">Callsign or id.</param>
/// <param name="Detail">"category · heading°".</param>
/// <param name="Altitude">Its altitude.</param>
public sealed record TrafficRow(string Id, string Name, string Detail, string Altitude);

/// <summary>
/// The 3D view activity: what the scene shows (the plan with its planned home as the datum, the active vehicle, the
/// environment's traffic, zones and points of interest, the plan's terrain), the traffic list, the camera choices
/// (follow an aircraft, or fit everything seen from above) and the telemetry panel. It holds Station data only; the view
/// turns it into the scene.
/// </summary>
public sealed partial class Scene3DViewModel : ObservableObject, IDisposable
{
    private readonly PlanStore _plans;
    private readonly VehicleRegistry _vehicles;
    private readonly EnvironmentStore _environment;
    private readonly TerrainService _terrain;
    private readonly UnitService _units;
    private readonly IUiDispatcher _ui;
    private Vehicle? _vehicle;

    /// <summary>Follows the plan, the active vehicle, the environment and the terrain.</summary>
    public Scene3DViewModel(TelemetryPanelViewModel telemetry, PlanStore plans, VehicleRegistry vehicles, EnvironmentStore environment, TerrainService terrain,
        UnitService units, IUiDispatcher ui)
    {
        Telemetry = telemetry;
        _plans = plans;
        _vehicles = vehicles;
        _environment = environment;
        _terrain = terrain;
        _units = units;
        _ui = ui;
        Plan = plans.Current;
        Terrain = terrain.Grid;
        _plans.Changed += OnPlanChanged;
        _vehicles.ActiveChanged += OnActiveChanged;
        _environment.Changed += OnEnvironmentChanged;
        _terrain.GridChanged += OnGridChanged;
        ReadEnvironment();
        Follow(vehicles.Active);
    }

    /// <summary>Asks the view to frame everything from above.</summary>
    public event Action? FitRequested;

    /// <summary>The telemetry panel.</summary>
    public TelemetryPanelViewModel Telemetry { get; }

    /// <summary>The credits the terrain data's licence requires.</summary>
    public string Credits => Strings.TerrainCredit;

    /// <summary>The plan: route, fences and the datum.</summary>
    [ObservableProperty] public partial Plan Plan { get; private set; }

    /// <summary>The active vehicle; null without one.</summary>
    [ObservableProperty] public partial VehicleSnapshot? Ownship { get; private set; }

    /// <summary>Traffic from the environment.</summary>
    [ObservableProperty] public partial IReadOnlyList<TrafficTarget> Traffic { get; private set; } = [];

    /// <summary>Zones from the environment.</summary>
    [ObservableProperty] public partial IReadOnlyList<Zone> Zones { get; private set; } = [];

    /// <summary>Points of interest from the environment.</summary>
    [ObservableProperty] public partial IReadOnlyList<PointOfInterest> Points { get; private set; } = [];

    /// <summary>Terrain around the plan; null without data.</summary>
    [ObservableProperty] public partial TerrainGrid? Terrain { get; private set; }

    /// <summary>Terrain height above sea level at a point, for above-terrain plans; null when unknown.</summary>
    public double? GroundAt(Ketras.Model.Geo.GeoPoint point) => _terrain.GroundAt(point);

    /// <summary>The followed aircraft's scene id; null when the camera follows nothing.</summary>
    [ObservableProperty] public partial string? Followed { get; private set; }

    /// <summary>The traffic list.</summary>
    public ObservableCollection<TrafficRow> TrafficRows { get; } = [];

    /// <summary>There is traffic to list.</summary>
    public bool HasTraffic => TrafficRows.Count > 0;

    /// <summary>The number of aircraft listed.</summary>
    public string TrafficCount => TrafficRows.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>The scene id of a vehicle.</summary>
    public static string SceneId(VehicleId id) => string.Create(CultureInfo.InvariantCulture, $"vehicle-{id.SystemId}");

    /// <inheritdoc />
    public void Dispose()
    {
        _plans.Changed -= OnPlanChanged;
        _vehicles.ActiveChanged -= OnActiveChanged;
        _environment.Changed -= OnEnvironmentChanged;
        _terrain.GridChanged -= OnGridChanged;
        Follow(null);
    }

    /// <summary>The operator moved the camera away from the followed aircraft.</summary>
    public void ReleaseFollow() => Followed = null;

    [RelayCommand]
    private void Fit()
    {
        Followed = null;
        FitRequested?.Invoke();
    }

    [RelayCommand]
    private void FollowOwnship() => Followed = Ownship is { } ownship ? SceneId(ownship.Id) : null;

    [RelayCommand]
    private void FollowTraffic(TrafficRow row) => Followed = row.Id;

    private void OnPlanChanged(Plan plan, PlanChange change) => _ui.Post(() => Plan = plan);

    private void OnGridChanged(TerrainGrid? grid) => _ui.Post(() => Terrain = grid);

    private void OnEnvironmentChanged(EnvironmentChange change)
    {
        if ((change & (EnvironmentChange.Traffic | EnvironmentChange.Zones | EnvironmentChange.Points)) != 0)
        {
            _ui.Post(ReadEnvironment);
        }
    }

    private void ReadEnvironment()
    {
        Traffic = _environment.Traffic;
        Zones = _environment.Zones;
        Points = _environment.Points;
        TrafficRows.Clear();
        foreach (var target in Traffic.OrderBy(t => t.Callsign ?? t.Id, StringComparer.OrdinalIgnoreCase))
        {
            TrafficRows.Add(new TrafficRow(
                target.Id,
                target.Callsign ?? target.Id,
                string.Create(CultureInfo.CurrentCulture, $"{target.Category} · {target.HeadingDegrees ?? 0:F0}°"),
                target.AltitudeMeters is { } altitude ? _units.Altitude(altitude) : "--"));
        }

        if (Followed is { } followed && !followed.StartsWith("vehicle-", StringComparison.Ordinal) && TrafficRows.All(r => r.Id != followed))
        {
            Followed = null;
        }

        OnPropertyChanged(nameof(HasTraffic));
        OnPropertyChanged(nameof(TrafficCount));
    }

    private void OnActiveChanged(Vehicle? vehicle) => _ui.Post(() => Follow(vehicle));

    private void Follow(Vehicle? vehicle)
    {
        if (_vehicle is not null)
        {
            _vehicle.SnapshotChanged -= OnSnapshot;
        }

        _vehicle = vehicle;
        if (vehicle is not null)
        {
            vehicle.SnapshotChanged += OnSnapshot;
        }

        Ownship = vehicle?.Snapshot;
    }

    private void OnSnapshot(VehicleSnapshot snapshot) => _ui.Post(() => Ownship = snapshot);
}
