using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ketras.Model.Environment;
using Ketras.Model.Planning;
using Ketras.Model.Terrain;
using Ketras.Model.Vehicles;
using Ketras.Resources;
using Ketras.Station.Environment;
using Ketras.Station.Imagery;
using Ketras.Station.Missions;
using Ketras.Station.Settings;
using Ketras.Station.Terrain;
using Ketras.Station.Vehicles;
using Ketras.Telemetry;
using Ketras.Utilities;

namespace Ketras.Scene3D;

/// <summary>One aircraft of the traffic list.</summary>
/// <param name="Id">Its id, as the scene knows it.</param>
/// <param name="Name">Callsign or id.</param>
/// <param name="Detail">"category · heading°".</param>
/// <param name="Altitude">Its altitude.</param>
public sealed record TrafficRow(string Id, string Name, string Detail, string Altitude);

/// <summary>How the 3D view paints the terrain.</summary>
public enum SceneSurface
{
    /// <summary>Grey relief with contour lines.</summary>
    Grey,

    /// <summary>Satellite imagery.</summary>
    Satellite,

    /// <summary>The street map.</summary>
    Street,

    /// <summary>The operator's tile source (Settings), which replaces satellite and street while it is set.</summary>
    Custom
}

/// <summary>
/// The 3D view activity: what the scene shows (the plan with its planned home as the datum, the active vehicle, the
/// environment's traffic, zones and points of interest, the plan's terrain), the traffic list, the camera choices
/// (follow an aircraft, or fit everything seen from above), the terrain surface (grey relief or map imagery, saved in the
/// settings) and the telemetry panel. It holds Station data only; the view turns it into the scene.
/// </summary>
public sealed partial class Scene3DViewModel : ObservableObject, IDisposable
{
    private readonly PlanStore _plans;
    private readonly VehicleRegistry _vehicles;
    private readonly EnvironmentStore _environment;
    private readonly TerrainService _terrain;
    private readonly MapTileService _tiles;
    private readonly SettingsStore _settings;
    private readonly UnitService _units;
    private readonly IUiDispatcher _ui;
    private Vehicle? _vehicle;
    private CancellationTokenSource? _imageryLoad;
    private CancellationTokenSource? _detailLoad;
    private TileSource? _source;

    /// <summary>The terrain surface, saved by name.</summary>
    public static SettingDefinition<string> SurfaceSetting { get; } = new("scene.surface", nameof(SceneSurface.Grey));

    /// <summary>Follows the plan, the active vehicle, the environment, the terrain and the surface setting.</summary>
    public Scene3DViewModel(TelemetryPanelViewModel telemetry, PlanStore plans, VehicleRegistry vehicles, EnvironmentStore environment, TerrainService terrain,
        MapTileService tiles, SettingsStore settings, UnitService units, IUiDispatcher ui)
    {
        Telemetry = telemetry;
        _plans = plans;
        _vehicles = vehicles;
        _environment = environment;
        _terrain = terrain;
        _tiles = tiles;
        _settings = settings;
        _units = units;
        _ui = ui;
        Plan = plans.Current;
        Terrain = terrain.Grid;
        ReadSurface();
        _settings.Changed += OnSetting;
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

    /// <summary>The credits the terrain data's licence requires, and the imagery's while it is shown.</summary>
    public string Credits => Imagery is { } imagery && Surface != SceneSurface.Grey
        ? Strings.TerrainCredit + System.Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Strings.SceneImageryCredit, imagery.Attribution)
        : Strings.TerrainCredit;

    /// <summary>How the terrain is painted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGrey), nameof(IsSatellite), nameof(IsStreet), nameof(IsCustom), nameof(Credits))]
    public partial SceneSurface Surface { get; private set; }

    /// <summary>Grey relief is shown.</summary>
    public bool IsGrey => Surface == SceneSurface.Grey;

    /// <summary>Satellite imagery is shown.</summary>
    public bool IsSatellite => Surface == SceneSurface.Satellite;

    /// <summary>The street map is shown.</summary>
    public bool IsStreet => Surface == SceneSurface.Street;

    /// <summary>The custom map is shown.</summary>
    public bool IsCustom => Surface == SceneSurface.Custom;

    /// <summary>A custom tile source is set: it replaces satellite and street.</summary>
    [ObservableProperty] public partial bool HasCustomTiles { get; private set; }

    /// <summary>The map tiles over the terrain; null in grey or while none are loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Credits))]
    public partial ImageryMosaic? Imagery { get; private set; }

    /// <summary>Finer tiles around where the camera looks (<see cref="RequestImageryDetail"/>); null without any.</summary>
    [ObservableProperty] public partial ImageryMosaic? ImageryDetail { get; private set; }

    /// <summary>
    /// The scene asks for finer tiles in <paramref name="range"/> around where its camera looks, or for none (null).
    /// A newer request cancels the one before; a block from another source than the one shown is not asked for.
    /// </summary>
    public void RequestImageryDetail(Ketras.Model.Geo.TileRange? range)
    {
        _detailLoad?.Cancel();
        _detailLoad = null;
        if (range is not { } block || _source is not { } source || Imagery is null || block.Zoom > source.MaxZoom)
        {
            ImageryDetail = null;
            return;
        }

        var load = _detailLoad = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var mosaic = await _tiles.LoadAsync(source, block, load.Token);
                _ui.Post(() =>
                {
                    if (_detailLoad == load && _source == source)
                    {
                        ImageryDetail = mosaic;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

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
        _settings.Changed -= OnSetting;
        _imageryLoad?.Cancel();
        _imageryLoad?.Dispose();
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
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
    private void ShowGrey() => ChooseSurface(SceneSurface.Grey);

    [RelayCommand]
    private void ShowSatellite() => ChooseSurface(SceneSurface.Satellite);

    [RelayCommand]
    private void ShowStreet() => ChooseSurface(SceneSurface.Street);

    [RelayCommand]
    private void ShowCustom() => ChooseSurface(SceneSurface.Custom);

    private void ChooseSurface(SceneSurface surface)
    {
        _settings.Set(SurfaceSetting, surface.ToString());
        ReadSurface();
    }

    /// <summary>The saved surface; satellite or street become custom while a custom source is set, and back.</summary>
    private void ReadSurface()
    {
        HasCustomTiles = _settings.Get(Settings.AppSettings.CustomTileUrl).Length > 0;
        var saved = Enum.TryParse<SceneSurface>(_settings.Get(SurfaceSetting), out var surface) ? surface : SceneSurface.Grey;
        Surface = saved == SceneSurface.Grey ? SceneSurface.Grey
            : HasCustomTiles ? SceneSurface.Custom
            : saved == SceneSurface.Custom ? SceneSurface.Satellite
            : saved;
    }

    private void OnSetting(string key)
    {
        if (key == Settings.AppSettings.CustomTileUrl.Key || key == Settings.AppSettings.CustomTileAttribution.Key)
        {
            _ui.Post(() =>
            {
                ReadSurface();
                LoadImagery();
            });
        }
    }

    partial void OnSurfaceChanged(SceneSurface value) => LoadImagery();

    partial void OnTerrainChanged(TerrainGrid? value) => LoadImagery();

    /// <summary>Fetches the tiles over the terrain for the surface; a newer request cancels the one before.</summary>
    private void LoadImagery()
    {
        _imageryLoad?.Cancel();
        _imageryLoad = null;
        _detailLoad?.Cancel();
        _detailLoad = null;
        ImageryDetail = null;
        var source = _source = Surface switch
        {
            SceneSurface.Satellite => TileSource.Satellite,
            SceneSurface.Street => TileSource.Street,
            SceneSurface.Custom => TileSource.Custom(_settings.Get(Settings.AppSettings.CustomTileUrl), _settings.Get(Settings.AppSettings.CustomTileAttribution)),
            _ => null
        };
        if (source is null || Terrain is not { } grid)
        {
            Imagery = null;
            return;
        }

        var load = _imageryLoad = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var mosaic = await _tiles.LoadAsync(source, grid.SouthLatitude, grid.WestLongitude, grid.NorthLatitude, grid.EastLongitude, load.Token);
                _ui.Post(() =>
                {
                    if (_imageryLoad == load)
                    {
                        Imagery = mosaic;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
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
